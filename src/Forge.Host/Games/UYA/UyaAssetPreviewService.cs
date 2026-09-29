using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Forge.Host.Domain;
using RatchetPs2.Core.Textures.Pif;
using RatchetPs2.Core.Textures.Png;
using RatchetPs2.Core.Wad.Models;

namespace Forge.Host.Games.UYA;

public static class UyaAssetPreviewService
{
    public const int SchemaVersion = 1;
    private const int TextureRasterSchemaVersion = 2;
    private const string MarkerName = ".forge-asset-preview.json";
    private const long MaxTextureBytes = 64L * 1024 * 1024;
    // ponytail: bounded lock striping avoids per-key lock retention; increase stripes only if collisions profile hot.
    private static readonly SemaphoreSlim[] WriteLocks = Enumerable.Range(0, 16)
        .Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<UyaAssetPreviewResult> PrepareAsync(
        UyaAssetPreviewRequest request,
        string sdkRevision,
        CancellationToken cancellationToken = default)
    {
        Validate(request, sdkRevision);
        var cacheKey = CreateCacheKey(request, sdkRevision);
        var target = Path.Combine(Path.GetFullPath(request.CacheRootPath), cacheKey);
        var cached = await TryOpenAsync(target, request, sdkRevision, cancellationToken);
        if (cached is not null) return cached with { CacheHit = true };
        var catalog = await AssetCatalogStore.OpenAsync(request.CatalogRootPath, cancellationToken);
        var entry = catalog.Query(new(Id: request.AssetId)).SingleOrDefault()
            ?? throw new InvalidDataException($"Asset {request.AssetId} is not present in the catalog.");
        if (entry.Kind != request.Kind) throw new InvalidDataException("Asset kind does not match the catalog entry.");
        var path = catalog.ResolveBlobPath(entry.Id) ?? throw new FileNotFoundException("Asset blob is missing.");
        var package = entry.Kind == AssetKind.Texture
            ? await BuildTexturePackageAsync(entry, path, cancellationToken)
            : await UyaRenderPackageService.BuildAssetPackageAsync(
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

        var writeLock = WriteLocks[Uri.FromHex(cacheKey[^1])];
        await writeLock.WaitAsync(cancellationToken);
        try
        {
            cached = await TryOpenAsync(target, request, sdkRevision, cancellationToken);
            if (cached is not null) return cached with { CacheHit = true };
            var entries = UyaRenderPackageService.ValidateEntries(package);
            var previewPath = request.Kind == AssetKind.Texture ? "texture.png" : "model.gltf";
            if (entries.Count(entry => entry.Path == previewPath) != 1)
                throw new InvalidDataException($"Asset preview package must contain {previewPath}.");
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
                    previewPath);
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
            writeLock.Release();
        }
    }

    internal static string CreateCacheKey(UyaAssetPreviewRequest request, string sdkRevision)
    {
        Validate(request, sdkRevision);
        var schema = request.Kind == AssetKind.Texture ? TextureRasterSchemaVersion : SchemaVersion;
        var identity = $"{request.AssetId}\n{request.Kind}\n{sdkRevision}\n{schema}\n{request.ViewPreset}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..24];
        return $"uya-preview-{hash}";
    }

    private static async Task<PackedFilePackage> BuildTexturePackageAsync(
        AssetCatalogEntry entry,
        string path,
        CancellationToken cancellationToken)
    {
        if (entry.CanonicalFormatVersion != UyaAssetImportService.TextureCanonicalFormatVersion)
            throw new InvalidDataException($"Unsupported canonical texture format {entry.CanonicalFormatVersion}.");
        var info = new FileInfo(path);
        if (info.Length != entry.Size || info.Length is <= 0 or > MaxTextureBytes)
            throw new InvalidDataException("Texture blob size does not match its catalog entry.");
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (AssetId.Compute(entry.Kind, entry.CanonicalFormatVersion, bytes) != entry.Id)
            throw new InvalidDataException("Texture blob failed its identity check.");
        var png = await Task.Run(
            () => PifAssetExporter.Export(bytes, options: new() { DoubleAlpha = true }).PngBytes,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return PackedFilePackageBuilder.Pack([new("texture.png", png, "image/png")]);
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
            if (request.Kind == AssetKind.Texture)
            {
                await using var stream = new FileStream(
                    UyaRenderPackageService.ResolveEntryPath(root, marker.ModelPath),
                    FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                _ = PngTextureMetadataReader.ReadPng(stream);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or IOException or OverflowException)
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
        if (request.Kind is not (AssetKind.Moby or AssetKind.Tie or AssetKind.Shrub or AssetKind.Texture))
            throw new NotSupportedException($"{request.Kind} previews are not supported.");
        if (request.AssetId.ToString().Length != AssetId.TextLength)
            throw new ArgumentException("Asset preview ID is invalid.", nameof(request));
        var preset = request.Kind == AssetKind.Texture ? "texture-default" : "model-default";
        if (request.ViewPreset != preset)
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
