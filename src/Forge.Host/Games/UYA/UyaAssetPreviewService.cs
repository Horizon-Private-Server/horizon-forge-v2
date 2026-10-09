using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Forge.Host.Domain;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.Skyboxes;
using RatchetPs2.Core.Textures.Pif;
using RatchetPs2.Core.Textures.Png;
using RatchetPs2.Core.Wad.Models;

namespace Forge.Host.Games.UYA;

public static class UyaAssetPreviewService
{
    public const int SchemaVersion = 1;
    private const int TextureRasterSchemaVersion = 2;
    private const int SkyRenderSchemaVersion = 2;
    private const string MarkerName = ".forge-asset-preview.json";
    // ponytail: bounded lock striping avoids per-key lock retention; increase stripes only if collisions profile hot.
    private static readonly SemaphoreSlim[] WriteLocks = Enumerable.Range(0, 16)
        .Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private static readonly object CatalogCacheLock = new();
    private static CatalogCache? CachedCatalog;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<AssetPreviewResult> PrepareAsync(
        AssetPreviewRequest request,
        string sdkRevision,
        CancellationToken cancellationToken = default)
    {
        Validate(request, sdkRevision);
        var cacheKey = CreateCacheKey(request, sdkRevision);
        var target = Path.Combine(Path.GetFullPath(request.CacheRootPath), cacheKey);
        var cached = await TryOpenAsync(target, request, sdkRevision, cancellationToken);
        if (cached is not null) return cached with { CacheHit = true };
        var catalog = await OpenCatalogAsync(request.CatalogRootPath, cancellationToken);
        var entry = catalog.Query(new(Id: request.AssetId)).SingleOrDefault();
        if (entry is not null && entry.Kind != request.Kind)
            throw new InvalidDataException("Asset kind does not match the catalog entry.");
        var package = entry?.Kind switch
        {
            AssetKind.Texture => await BuildTexturePackageAsync(
                entry,
                catalog.ResolveBlobPath(entry.Id) ?? throw new FileNotFoundException("Asset blob is missing."),
                cancellationToken),
            AssetKind.Sky => await BuildSkyPackageAsync(
                entry,
                catalog.ResolveBlobPath(entry.Id) ?? throw new FileNotFoundException("Asset blob is missing."),
                request.ShellIndex,
                cancellationToken),
            null when request.Kind == AssetKind.Texture && !string.IsNullOrWhiteSpace(request.ProjectPath) =>
                await BuildProjectTexturePackageAsync(request, cancellationToken),
            null => throw new InvalidDataException($"Asset {request.AssetId} is not present in the catalog or active project."),
            _ => await UyaRenderPackageService.BuildAssetPackageAsync(
                entry.Id,
                entry.Kind,
                entry.CanonicalFormatVersion,
                entry.Size,
                catalog.ResolveBlobPath(entry.Id) ?? throw new FileNotFoundException("Asset blob is missing."),
                cancellationToken),
        };
        return await MaterializeAsync(request, sdkRevision, package, cancellationToken);
    }

    internal static async Task<AssetPreviewResult> MaterializeAsync(
        AssetPreviewRequest request,
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
                    request.ShellIndex,
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

    internal static string CreateCacheKey(AssetPreviewRequest request, string sdkRevision)
    {
        Validate(request, sdkRevision);
        var schema = request.Kind switch
        {
            AssetKind.Texture => TextureRasterSchemaVersion,
            AssetKind.Sky => SkyRenderSchemaVersion,
            _ => SchemaVersion,
        };
        var identity = $"{request.AssetId}\n{request.Kind}\n{request.ShellIndex}\n{sdkRevision}\n{schema}\n{request.ViewPreset}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..24];
        return $"uya-preview-{hash}";
    }

    private static async Task<PackedFilePackage> BuildTexturePackageAsync(
        AssetCatalogEntry entry,
        string path,
        CancellationToken cancellationToken)
    {
        var bytes = await AssetCatalogBlobReader.ReadVerifiedAsync(
            entry, path, ProjectTextureAssetSchema.MaximumCanonicalBytes, cancellationToken);
        return await BuildTexturePackageAsync(entry.CanonicalFormatVersion, bytes, cancellationToken);
    }

    private static async Task<PackedFilePackage> BuildProjectTexturePackageAsync(
        AssetPreviewRequest request,
        CancellationToken cancellationToken)
    {
        var projectRoot = Path.GetFullPath(request.ProjectPath);
        var id = request.AssetId.ToString();
        var path = Path.Combine(projectRoot, "assets", id[..2], $"{id}.blob");
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException($"Project asset blob {request.AssetId} is missing.");
        if (info.Length is <= 0 or > ProjectTextureAssetSchema.MaximumCanonicalBytes)
            throw new InvalidDataException($"Project texture {request.AssetId} has an invalid size.");
        var bytes = await AssetCatalogBlobReader.ReadVerifiedAsync(
            request.AssetId,
            AssetKind.Texture,
            ProjectTextureAssetSchema.CanonicalFormatVersion,
            info.Length,
            path,
            ProjectTextureAssetSchema.MaximumCanonicalBytes,
            cancellationToken);
        return await BuildTexturePackageAsync(ProjectTextureAssetSchema.CanonicalFormatVersion, bytes, cancellationToken);
    }

    private static async Task<PackedFilePackage> BuildTexturePackageAsync(
        uint canonicalFormatVersion,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        if (canonicalFormatVersion != UyaAssetImportService.TextureCanonicalFormatVersion)
            throw new InvalidDataException($"Unsupported canonical texture format {canonicalFormatVersion}.");
        byte[] png;
        try
        {
            png = await Task.Run(
                () => PifAssetExporter.Export(bytes, options: new() { DoubleAlpha = true }).PngBytes,
                cancellationToken);
        }
        catch (Exception exception) when (exception is EndOfStreamException or ArgumentException or OverflowException)
        {
            throw new InvalidDataException("Texture preview decoding failed because the canonical PIF is invalid.", exception);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return PackedFilePackageBuilder.Pack([new("texture.png", png, "image/png")]);
    }

    private static async Task<AssetCatalogStore> OpenCatalogAsync(
        string rootPath,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(rootPath);
        var catalogPath = Path.Combine(root, "catalog-v0.json");
        var info = new FileInfo(catalogPath);
        var length = info.Exists ? info.Length : -1;
        var modified = info.Exists ? info.LastWriteTimeUtc.Ticks : 0;
        Task<AssetCatalogStore> opening;
        lock (CatalogCacheLock)
        {
            if (CachedCatalog is { } cached
                && cached.RootPath == root
                && cached.Length == length
                && cached.Modified == modified)
                opening = cached.Opening;
            else
            {
                opening = AssetCatalogStore.OpenAsync(root);
                CachedCatalog = new(root, length, modified, opening);
            }
        }
        try
        {
            return await opening.WaitAsync(cancellationToken);
        }
        catch
        {
            lock (CatalogCacheLock)
                if (CachedCatalog?.Opening == opening) CachedCatalog = null;
            throw;
        }
    }

    private static async Task<PackedFilePackage> BuildSkyPackageAsync(
        AssetCatalogEntry entry,
        string path,
        int? shellIndex,
        CancellationToken cancellationToken)
    {
        var bytes = await AssetCatalogBlobReader.ReadVerifiedAsync(
            entry, path, ForgeProjectWorkspace.MaxAttachedAssetBytes, cancellationToken);
        return await Task.Run(() =>
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var sky = SkyboxReader.Read(stream, GameId.UYA);
            IReadOnlyList<SkyboxShell> shells = shellIndex is { } index
                ? [sky.Shells.SingleOrDefault(value => value.Index == index)
                    ?? throw new InvalidDataException($"Sky shell {index} is not present in the asset.")]
                : sky.Shells;
            var textureIds = shells.SelectMany(shell => shell.Clusters).SelectMany(cluster => cluster.Triangles)
                .Select(triangle => (int)triangle.TextureId).ToHashSet();
            var textures = sky.Textures.Where(texture => textureIds.Contains(texture.Index)).ToArray();
            var selected = new Skybox(
                sky.Header with
                {
                    ShellCount = checked((short)shells.Count),
                    SpriteCount = 0,
                    SpriteMax = 0,
                    TextureCount = checked((short)textures.Length),
                },
                shells, textures, [], sky.FxList, sky.ByteLength);
            var export = SkyboxGltfExporter.Export(selected, "model.gltf",
                SkyboxGameProfile.ForGame(GameId.UYA).CreateExportOptions(
                    "model.buffer.bin", null, 1, includeDiagnostics: false, minify: true));
            return PackedFilePackageBuilder.Pack([
                new("model.gltf", export.GltfBytes, "model/gltf+json"),
                new("model.buffer.bin", export.BinBytes, "application/octet-stream"),
                .. export.Textures.Select(texture =>
                    new PackedFile($"textures/{texture.FileName}", texture.PngBytes, "image/png")),
            ]);
        }, cancellationToken);
    }

    private static async Task<AssetPreviewResult?> TryOpenAsync(
        string root,
        AssetPreviewRequest request,
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
            || marker.ShellIndex != request.ShellIndex
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

    private static void Validate(AssetPreviewRequest request, string sdkRevision)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CacheRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CatalogRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TargetGame);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ViewPreset);
        ArgumentException.ThrowIfNullOrWhiteSpace(sdkRevision);
        if (request.TargetGame != "UYA") throw new NotSupportedException("Asset preview target must be UYA.");
        if (request.Kind is not (AssetKind.Moby or AssetKind.Tie or AssetKind.Shrub or AssetKind.Texture
            or AssetKind.Sky or AssetKind.Collision))
            throw new NotSupportedException($"{request.Kind} previews are not supported.");
        if (request.AssetId.ToString().Length != AssetId.TextLength)
            throw new ArgumentException("Asset preview ID is invalid.", nameof(request));
        if (request.Kind == AssetKind.Sky && request.ShellIndex is not null and not (>= 0 and < 8))
            throw new ArgumentException("Sky preview shell index is invalid.", nameof(request));
        if (request.Kind != AssetKind.Sky && request.ShellIndex is not null)
            throw new ArgumentException("Only sky previews accept a shell index.", nameof(request));
        var preset = request.Kind switch
        {
            AssetKind.Texture => "texture-default",
            AssetKind.Sky => "sky-default",
            AssetKind.Collision => "collision-default",
            _ => "model-default",
        };
        if (request.ViewPreset != preset)
            throw new ArgumentException("Asset preview view preset is invalid.", nameof(request));
    }

    private sealed record CacheMarker(
        int SchemaVersion,
        string CacheKey,
        string AssetId,
        AssetKind Kind,
        int? ShellIndex,
        string ViewPreset,
        string SdkRevision,
        IReadOnlyList<CacheFile> Files,
        string ModelPath);

    private sealed record CatalogCache(
        string RootPath,
        long Length,
        long Modified,
        Task<AssetCatalogStore> Opening);

    private sealed record CacheFile(string Path, long Length);
}
