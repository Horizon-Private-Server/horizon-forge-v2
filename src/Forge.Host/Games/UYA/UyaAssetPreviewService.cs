using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Forge.Host.Domain;
using RatchetPs2.Core.Wad.Models;

namespace Forge.Host.Games.UYA;

public static class UyaAssetPreviewService
{
    public const int SchemaVersion = 1;
    private const string MarkerName = ".forge-asset-preview.json";
    // ponytail: one write lock is enough for v0; replace with per-cache-key locks if preview writes contend.
    private static readonly SemaphoreSlim Writes = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<UyaAssetPreviewResult> PrepareAsync(
        UyaAssetPreviewRequest request,
        string sdkRevision,
        CancellationToken cancellationToken = default)
    {
        Validate(request, sdkRevision);
        var catalog = await AssetCatalogStore.OpenAsync(request.CatalogRootPath, cancellationToken);
        var entry = catalog.Query(new(Id: request.AssetId)).SingleOrDefault()
            ?? throw new InvalidDataException($"Asset {request.AssetId} is not present in the catalog.");
        if (entry.Kind != request.Kind) throw new InvalidDataException("Asset kind does not match the catalog entry.");
        var cacheKey = CreateCacheKey(request, sdkRevision);
        var target = Path.Combine(Path.GetFullPath(request.CacheRootPath), cacheKey);
        var cached = await TryOpenAsync(target, request, sdkRevision, cancellationToken);
        if (cached is not null) return cached with { CacheHit = true };
        var path = catalog.ResolveBlobPath(entry.Id) ?? throw new FileNotFoundException("Asset blob is missing.");
        var package = await UyaRenderPackageService.BuildAssetPackageAsync(
            entry.Id, entry.Kind, entry.CanonicalFormatVersion, entry.Size, path, cancellationToken);
        return await MaterializeAsync(request, sdkRevision, package, cancellationToken);
    }

    internal static async Task<UyaAssetPreviewResult> MaterializeAsync(
        UyaAssetPreviewRequest request,
        string sdkRevision,
        PackedFilePackage package,
        CancellationToken cancellationToken = default)
    {
        Validate(request, sdkRevision);
        ArgumentNullException.ThrowIfNull(package);
        var cacheRoot = Path.GetFullPath(request.CacheRootPath);
        var cacheKey = CreateCacheKey(request, sdkRevision);
        var target = Path.Combine(cacheRoot, cacheKey);
        var cached = await TryOpenAsync(target, request, sdkRevision, cancellationToken);
        if (cached is not null) return cached with { CacheHit = true };

        await Writes.WaitAsync(cancellationToken);
        try
        {
            cached = await TryOpenAsync(target, request, sdkRevision, cancellationToken);
            if (cached is not null) return cached with { CacheHit = true };
            var entries = UyaRenderPackageService.ValidateEntries(package);
            if (entries.Count(entry => entry.Path == "model.gltf") != 1)
                throw new InvalidDataException("Asset preview package must contain model.gltf.");
            Directory.CreateDirectory(cacheRoot);
            var partial = Path.Combine(cacheRoot, $".{cacheKey}.{Guid.NewGuid():N}.partial");
            Directory.CreateDirectory(partial);
            try
            {
                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var destination = UyaRenderPackageService.ResolveEntryPath(partial, entry.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    await File.WriteAllBytesAsync(
                        destination,
                        package.PackedBytes.AsMemory(entry.Offset, entry.Length),
                        cancellationToken);
                }
                var marker = new CacheMarker(
                    SchemaVersion,
                    cacheKey,
                    request.AssetId.ToString(),
                    request.Kind,
                    request.ViewPreset,
                    sdkRevision,
                    entries.Select(entry => new CacheFile(entry.Path, entry.Length)).ToArray(),
                    "model.gltf");
                await File.WriteAllBytesAsync(
                    Path.Combine(partial, MarkerName),
                    JsonSerializer.SerializeToUtf8Bytes(marker, JsonOptions),
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                Directory.Move(partial, target);
                return new(target, cacheKey, marker.ModelPath, false);
            }
            catch
            {
                if (Directory.Exists(partial)) Directory.Delete(partial, recursive: true);
                throw;
            }
        }
        finally
        {
            Writes.Release();
        }
    }

    internal static string CreateCacheKey(UyaAssetPreviewRequest request, string sdkRevision)
    {
        Validate(request, sdkRevision);
        var identity = $"{request.AssetId}\n{request.Kind}\n{sdkRevision}\n{SchemaVersion}\n{request.ViewPreset}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..24];
        return $"uya-preview-{hash}";
    }

    private static async Task<UyaAssetPreviewResult?> TryOpenAsync(
        string root,
        UyaAssetPreviewRequest request,
        string sdkRevision,
        CancellationToken cancellationToken)
    {
        var markerPath = Path.Combine(root, MarkerName);
        if (!File.Exists(markerPath) || new FileInfo(markerPath).Length > 1024 * 1024) return null;
        CacheMarker? marker;
        try
        {
            marker = JsonSerializer.Deserialize<CacheMarker>(await File.ReadAllBytesAsync(markerPath, cancellationToken));
        }
        catch (JsonException)
        {
            return null;
        }
        if (marker is null
            || marker.SchemaVersion != SchemaVersion
            || marker.CacheKey != Path.GetFileName(root)
            || marker.AssetId != request.AssetId.ToString()
            || marker.Kind != request.Kind
            || marker.ViewPreset != request.ViewPreset
            || marker.SdkRevision != sdkRevision
            || marker.Files is null
            || marker.Files.Count is 0 or > 1_024)
            return null;
        try
        {
            var files = marker.Files.ToDictionary(file => UyaRenderPackageService.NormalizeEntryPath(file.Path),
                StringComparer.Ordinal);
            if (!files.ContainsKey(UyaRenderPackageService.NormalizeEntryPath(marker.ModelPath))) return null;
            foreach (var (path, file) in files)
            {
                var candidate = UyaRenderPackageService.ResolveEntryPath(root, path);
                if (file.Length < 0 || !File.Exists(candidate) || new FileInfo(candidate).Length != file.Length) return null;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        {
            return null;
        }
        return new(root, marker.CacheKey, marker.ModelPath, true);
    }

    private static void Validate(UyaAssetPreviewRequest request, string sdkRevision)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CacheRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CatalogRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TargetGame);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ViewPreset);
        ArgumentException.ThrowIfNullOrWhiteSpace(sdkRevision);
        if (request.TargetGame != "UYA") throw new NotSupportedException("Asset preview target must be UYA.");
        if (request.Kind is not (AssetKind.Moby or AssetKind.Tie or AssetKind.Shrub))
            throw new NotSupportedException($"{request.Kind} previews are not supported.");
        if (request.AssetId.ToString().Length != AssetId.TextLength)
            throw new ArgumentException("Asset preview ID is invalid.", nameof(request));
        if (request.ViewPreset != "model-default")
            throw new ArgumentException("Asset preview view preset is invalid.", nameof(request));
    }

    private sealed record CacheMarker(
        int SchemaVersion,
        string CacheKey,
        string AssetId,
        AssetKind Kind,
        string ViewPreset,
        string SdkRevision,
        IReadOnlyList<CacheFile> Files,
        string ModelPath);

    private sealed record CacheFile(string Path, long Length);
}
