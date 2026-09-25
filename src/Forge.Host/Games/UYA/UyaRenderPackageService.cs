using Forge.Host.Domain;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.LevelAssets;
using RatchetPs2.Core.Wad.Models;
using RatchetPs2.Games.DL.Level;
using RatchetPs2.Games.UYA.Level;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.UYA;

public static class UyaRenderPackageService
{
    public const int SchemaVersion = 5;
    private const long MaxAssetBytes = 256L * 1024 * 1024;
    private const string MarkerName = ".forge-render-package.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<UyaRenderPackageResult> PrepareAsync(
        UyaRenderPackageRequest request,
        string sdkRevision,
        Func<IsoProgress, ValueTask>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Validate(request, sdkRevision);
        var assets = await ResolveAssetsAsync(request, cancellationToken);
        var cacheKey = CreateCacheKey(request, sdkRevision, assets.Select(asset => asset.Id));
        var target = Path.Combine(Path.GetFullPath(request.CacheRootPath), cacheKey);
        var cached = await TryOpenAsync(target, request, sdkRevision, cancellationToken);
        if (cached is not null)
        {
            await ReportAsync(progress, 10_000, 10_000);
            return cached with { CacheHit = true };
        }
        if (string.IsNullOrWhiteSpace(request.SourceIsoPath) || !File.Exists(request.SourceIsoPath))
            throw new IOException("The scene cache is missing and the clean UYA ISO is unavailable. Repair it through Forge → Setup.");

        var identity = await UyaIsoService.ValidateAsync(
            request.SourceIsoPath,
            progress is null ? null : value => ReportScaledAsync(progress, value, 0, 2_000),
            cancellationToken);
        if (!identity.IsSupported || !identity.Fingerprint.Equals(request.Fingerprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The UYA source ISO does not match this project's verified fingerprint.");

        await using var iso = new FileStream(
            request.SourceIsoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        cancellationToken.ThrowIfCancellationRequested();
        await ReportAsync(progress, 2_000, 10_000);
        var levelWad = await Task.Run(
            () => LevelArchiveReader.ExtractPrimary(GameId.UYA, iso, request.Level),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await ReportAsync(progress, 3_000, 10_000);
        var packages = await Task.Run(() =>
        {
            var octants = UyaOcclusionGridReader.ReadLevelWad(levelWad).Octants.Select(value =>
                new UyaRenderOcclusionOctant(value.X, value.Y, value.Z, value.MaskIndex)).ToArray();
            return (
                Terrain: FrontendMapPackageBuilder.BuildLevelWadPart(
                    levelWad, GameId.UYA, FrontendMapAssetGroup.Terrain),
                Sky: FrontendMapPackageBuilder.BuildLevelWadPart(
                    levelWad, GameId.UYA, FrontendMapAssetGroup.Common),
                Environment: UyaRenderEnvironmentReader.Read(levelWad),
                Octants: (IReadOnlyList<UyaRenderOcclusionOctant>)octants);
        }, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await ReportAsync(progress, 5_000, 10_000);
        var renderedAssets = await BuildAssetsAsync(assets, progress, cancellationToken);
        return await MaterializeAsync(
            request, sdkRevision, cacheKey, packages.Terrain, packages.Sky, packages.Environment, packages.Octants,
            renderedAssets, progress, cancellationToken);
    }

    public static async Task<UyaRenderPackageResult> MaterializeAsync(
        UyaRenderPackageRequest request,
        string sdkRevision,
        PackedFilePackage package,
        Func<IsoProgress, ValueTask>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Validate(request, sdkRevision);
        ArgumentNullException.ThrowIfNull(package);
        var cacheKey = CreateCacheKey(request, sdkRevision);
        return await MaterializeAsync(
            request, sdkRevision, cacheKey, package, null, null, [], [], progress, cancellationToken);
    }

    public static async Task<UyaRenderPackageResult> MaterializeAsync(
        UyaRenderPackageRequest request,
        string sdkRevision,
        PackedFilePackage terrainPackage,
        PackedFilePackage skyPackage,
        UyaRenderEnvironmentResult environment,
        Func<IsoProgress, ValueTask>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Validate(request, sdkRevision);
        ArgumentNullException.ThrowIfNull(terrainPackage);
        ArgumentNullException.ThrowIfNull(skyPackage);
        ArgumentNullException.ThrowIfNull(environment);
        var cacheKey = CreateCacheKey(request, sdkRevision);
        return await MaterializeAsync(
            request, sdkRevision, cacheKey, terrainPackage, skyPackage, environment, [], [],
            progress, cancellationToken);
    }

    private static async Task<UyaRenderPackageResult> MaterializeAsync(
        UyaRenderPackageRequest request,
        string sdkRevision,
        string cacheKey,
        PackedFilePackage terrainPackage,
        PackedFilePackage? skyPackage,
        UyaRenderEnvironmentResult? environment,
        IReadOnlyList<UyaRenderOcclusionOctant> octants,
        IReadOnlyList<BuiltAsset> assets,
        Func<IsoProgress, ValueTask>? progress,
        CancellationToken cancellationToken)
    {
        var cacheRoot = Path.GetFullPath(request.CacheRootPath);
        var target = Path.Combine(cacheRoot, cacheKey);
        var cached = await TryOpenAsync(target, request, sdkRevision, cancellationToken);
        if (cached is not null) return cached with { CacheHit = true };

        var files = ValidateEntries(terrainPackage)
            .Select(entry => new MaterialFile(
                entry.Path,
                terrainPackage.PackedBytes.AsMemory(entry.Offset, entry.Length)))
            .ToList();
        var terrainPaths = files.Select(entry => entry.Path)
            .Where(path => (path.StartsWith("tfrag/", StringComparison.Ordinal)
                    || path.Contains("/tfrag/", StringComparison.Ordinal))
                && path.EndsWith("/tfrag.gltf", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (terrainPaths.Length == 0) throw new InvalidDataException("The SDK render package contains no tfrag glTF files.");
        string? skyPath = null;
        if (skyPackage is not null)
        {
            var skyEntries = ValidateEntries(skyPackage)
                .Where(entry => entry.Path.StartsWith("assets/skybox/", StringComparison.Ordinal))
                .ToArray();
            foreach (var entry in skyEntries)
                files.Add(new(entry.Path, skyPackage.PackedBytes.AsMemory(entry.Offset, entry.Length)));
            var skyPaths = skyEntries.Select(entry => entry.Path)
                .Where(path => path.EndsWith("/skybox.gltf", StringComparison.Ordinal))
                .ToArray();
            if (skyPaths.Length > 1) throw new InvalidDataException("The SDK render package contains multiple sky glTF files.");
            skyPath = skyPaths.SingleOrDefault();
        }
        var assetResults = new List<UyaRenderAssetResult>(assets.Count);
        foreach (var asset in assets)
        {
            var kind = asset.Kind.ToString().ToLowerInvariant();
            if (asset.Package is null)
            {
                assetResults.Add(new(asset.Id.ToString(), kind, null, asset.Error ?? "Asset export failed."));
                continue;
            }
            var prefix = $"entities/{asset.Id}/";
            foreach (var entry in ValidateEntries(asset.Package))
                files.Add(new(
                    prefix + entry.Path,
                    asset.Package.PackedBytes.AsMemory(entry.Offset, entry.Length)));
            assetResults.Add(new(asset.Id.ToString(), kind, prefix + "model.gltf", null));
        }
        if (files.Select(file => file.Path).Distinct(StringComparer.Ordinal).Count() != files.Count)
            throw new InvalidDataException("The SDK render package contains duplicate paths.");

        Directory.CreateDirectory(cacheRoot);
        var partial = Path.Combine(cacheRoot, $".{cacheKey}.{Guid.NewGuid():N}.partial");
        Directory.CreateDirectory(partial);
        try
        {
            long written = 0;
            var total = Math.Max(1, files.Sum(entry => (long)entry.Bytes.Length));
            foreach (var entry in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = ResolveEntryPath(partial, entry.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var output = new FileStream(
                    destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await output.WriteAsync(entry.Bytes, cancellationToken);
                await output.FlushAsync(cancellationToken);
                written += entry.Bytes.Length;
                await ReportAsync(progress, 8_000 + written * 1_900 / total, 10_000);
            }

            var marker = new CacheMarker(
                SchemaVersion,
                cacheKey,
                request.Fingerprint.ToLowerInvariant(),
                request.Level,
                sdkRevision,
                files.Select(entry => new CacheFile(entry.Path, entry.Bytes.Length)).ToArray(),
                terrainPaths,
                skyPath,
                environment,
                assetResults,
                octants);
            await File.WriteAllBytesAsync(
                Path.Combine(partial, MarkerName),
                JsonSerializer.SerializeToUtf8Bytes(marker, JsonOptions),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(partial, target);
            await ReportAsync(progress, 10_000, 10_000);
            return new(target, cacheKey, terrainPaths, skyPath, environment, assetResults, false, octants);
        }
        catch
        {
            if (Directory.Exists(partial)) Directory.Delete(partial, recursive: true);
            throw;
        }
    }

    public static string CreateCacheKey(UyaRenderPackageRequest request, string sdkRevision)
        => CreateCacheKey(request, sdkRevision, []);

    private static string CreateCacheKey(
        UyaRenderPackageRequest request,
        string sdkRevision,
        IEnumerable<AssetId> assetIds)
    {
        Validate(request, sdkRevision);
        var identity = string.Join('\n', new[] { sdkRevision }
            .Concat(assetIds.Select(id => id.ToString()).Order(StringComparer.Ordinal)));
        var revisionKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant()[..16];
        return $"uya-{request.Fingerprint.ToLowerInvariant()}-l{request.Level:D2}-p{SchemaVersion}-r{revisionKey}";
    }

    private static async Task<UyaRenderPackageResult?> TryOpenAsync(
        string root,
        UyaRenderPackageRequest request,
        string sdkRevision,
        CancellationToken cancellationToken)
    {
        var markerPath = Path.Combine(root, MarkerName);
        if (!File.Exists(markerPath)) return null;
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
            || !marker.SourceFingerprint.Equals(request.Fingerprint, StringComparison.OrdinalIgnoreCase)
            || marker.Level != request.Level
            || marker.SdkRevision != sdkRevision
            || marker.Files is null
            || marker.Files.Count > 100_000
            || marker.TerrainPaths is null
            || marker.TerrainPaths.Count == 0
            || marker.Assets is null
            || marker.Assets.Count > 100_000
            || marker.OcclusionOctants is { Count: > 1_000_000 })
            return null;
        try
        {
            var files = marker.Files.ToDictionary(file => NormalizeEntryPath(file.Path), StringComparer.Ordinal);
            foreach (var (path, file) in files)
            {
                var candidate = ResolveEntryPath(root, path);
                if (file.Length < 0 || !File.Exists(candidate) || new FileInfo(candidate).Length != file.Length) return null;
            }
            foreach (var terrainPath in marker.TerrainPaths)
                if (!files.ContainsKey(NormalizeEntryPath(terrainPath))) return null;
            if (marker.SkyPath is not null && !files.ContainsKey(NormalizeEntryPath(marker.SkyPath))) return null;
            foreach (var asset in marker.Assets)
                if (asset.Path is not null && !files.ContainsKey(NormalizeEntryPath(asset.Path))) return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
        return new(root, marker.CacheKey, marker.TerrainPaths, marker.SkyPath, marker.Environment, marker.Assets, true,
            marker.OcclusionOctants);
    }

    private static IReadOnlyList<PackedFileEntry> ValidateEntries(PackedFilePackage package)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var entries = new PackedFileEntry[package.Entries.Count];
        var index = 0;
        foreach (var entry in package.Entries)
        {
            var normalized = NormalizeEntryPath(entry.Path);
            if (!paths.Add(normalized)) throw new InvalidDataException($"Duplicate render-package path '{entry.Path}'.");
            if (entry.Offset < 0 || entry.Length < 0 || (long)entry.Offset + entry.Length > package.PackedBytes.Length)
                throw new InvalidDataException($"Render-package entry '{entry.Path}' is outside its payload.");
            entries[index++] = entry with { Path = normalized };
        }
        return entries;
    }

    private static async Task<IReadOnlyList<AssetSource>> ResolveAssetsAsync(
        UyaRenderPackageRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProjectPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CatalogRootPath);
        var workspace = await ForgeProjectWorkspace.OpenAsync(request.ProjectPath, cancellationToken);
        if (workspace.Manifest.Target.Game != "UYA"
            || workspace.Manifest.BaseLevel.Level != request.Level
            || !workspace.Manifest.BaseLevel.SourceFingerprint.Equals(request.Fingerprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The active project does not match the requested UYA render package.");
        var catalog = await AssetCatalogStore.OpenAsync(request.CatalogRootPath, cancellationToken);
        var attached = workspace.Content.Assets.ToDictionary(asset => asset.Id);
        var result = new List<AssetSource>();
        foreach (var reference in workspace.Content.Entities
                     .Select(entity => entity.Asset)
                     .OfType<ProjectAssetReference>()
                     .Distinct()
                     .OrderBy(reference => reference.Id.ToString(), StringComparer.Ordinal))
        {
            if (attached.TryGetValue(reference.Id, out var projectAsset))
            {
                result.Add(new(
                    reference.Id,
                    reference.Kind,
                    projectAsset.CanonicalFormatVersion,
                    projectAsset.Size,
                    workspace.ResolveAssetPath(reference.Id, catalog),
                    projectAsset.Kind == reference.Kind ? null : "Project asset kind does not match its entity reference."));
                continue;
            }
            var global = catalog.Query(new(Id: reference.Id)).SingleOrDefault();
            result.Add(global is null
                ? new(reference.Id, reference.Kind, 0, 0, null, "Asset blob is missing from the global catalog.")
                : new(
                    reference.Id,
                    reference.Kind,
                    global.CanonicalFormatVersion,
                    global.Size,
                    catalog.ResolveBlobPath(reference.Id),
                    global.Kind == reference.Kind ? null : "Catalog asset kind does not match its entity reference."));
        }
        return result;
    }

    private static async Task<IReadOnlyList<BuiltAsset>> BuildAssetsAsync(
        IReadOnlyList<AssetSource> assets,
        Func<IsoProgress, ValueTask>? progress,
        CancellationToken cancellationToken)
    {
        var result = new List<BuiltAsset>(assets.Count);
        for (var index = 0; index < assets.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var asset = assets[index];
            try
            {
                if (asset.Error is not null) throw new InvalidDataException(asset.Error);
                if (asset.CanonicalFormatVersion is not 0
                    && asset.CanonicalFormatVersion != UyaAssetImportService.CanonicalFormatVersion)
                    throw new InvalidDataException($"Unsupported canonical asset format {asset.CanonicalFormatVersion}.");
                if (asset.Path is null || !File.Exists(asset.Path))
                    throw new FileNotFoundException("Asset blob is missing.");
                var info = new FileInfo(asset.Path);
                if (info.Length != asset.Size || info.Length is <= 0 or > MaxAssetBytes)
                    throw new InvalidDataException("Asset blob size does not match its catalog entry.");
                var bytes = await File.ReadAllBytesAsync(asset.Path, cancellationToken);
                if (AssetId.Compute(asset.Kind, asset.CanonicalFormatVersion, bytes) != asset.Id)
                    throw new InvalidDataException("Asset blob failed its identity check.");
                var canonical = UyaCanonicalAssetCodec.Decode(bytes);
                var kind = asset.Kind switch
                {
                    AssetKind.Moby => FrontendAssetKind.Moby,
                    AssetKind.Tie => FrontendAssetKind.Tie,
                    AssetKind.Shrub => FrontendAssetKind.Shrub,
                    _ => throw new NotSupportedException($"{asset.Kind} render assets are not supported."),
                };
                var package = await Task.Run(
                    () => FrontendAssetPackageBuilder.Build(
                        GameId.UYA, kind, canonical.ModelBytes, canonical.Textures),
                    cancellationToken);
                result.Add(new(asset.Id, asset.Kind, package, null));
            }
            catch (Exception exception) when (exception is ArgumentException
                or InvalidDataException
                or IOException
                or NotSupportedException
                or OverflowException)
            {
                result.Add(new(asset.Id, asset.Kind, null, exception.Message));
            }
            await ReportAsync(progress, 5_000 + (index + 1L) * 3_000 / Math.Max(1, assets.Count), 10_000);
        }
        return result;
    }

    private static string ResolveEntryPath(string root, string entryPath)
    {
        var normalized = NormalizeEntryPath(entryPath);
        var fullRoot = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
        var relative = Path.GetRelativePath(fullRoot, candidate);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
            throw new InvalidDataException($"Render-package path '{entryPath}' escapes its cache root.");
        return candidate;
    }

    private static string NormalizeEntryPath(string entryPath)
    {
        if (string.IsNullOrWhiteSpace(entryPath) || Path.IsPathRooted(entryPath))
            throw new InvalidDataException("Render-package paths must be relative.");
        var normalized = entryPath.Replace('\\', '/');
        if (normalized.Contains(':', StringComparison.Ordinal)
            || normalized.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new InvalidDataException($"Render-package path '{entryPath}' is invalid.");
        return normalized;
    }

    private static void Validate(UyaRenderPackageRequest request, string sdkRevision)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CacheRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Fingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(sdkRevision);
        if (request.Fingerprint.Length != 32 || !request.Fingerprint.All(Uri.IsHexDigit))
            throw new ArgumentException("Render-package source fingerprint is invalid.", nameof(request));
        if (request.Level < 0 || request.Level >= UyaLevelConstants.LevelInfoCount)
            throw new ArgumentOutOfRangeException(nameof(request), "Render-package level is out of range.");
    }

    private static ValueTask ReportAsync(Func<IsoProgress, ValueTask>? progress, long completed, long total) =>
        progress?.Invoke(new(completed, total)) ?? ValueTask.CompletedTask;

    private static ValueTask ReportScaledAsync(
        Func<IsoProgress, ValueTask> progress,
        IsoProgress value,
        long start,
        long end) => progress(new(
            value.Total <= 0 ? start : start + value.Completed * (end - start) / value.Total,
            10_000));

    private sealed record CacheMarker(
        int SchemaVersion,
        string CacheKey,
        string SourceFingerprint,
        int Level,
        string SdkRevision,
        IReadOnlyList<CacheFile> Files,
        IReadOnlyList<string> TerrainPaths,
        string? SkyPath,
        UyaRenderEnvironmentResult? Environment,
        IReadOnlyList<UyaRenderAssetResult> Assets,
        IReadOnlyList<UyaRenderOcclusionOctant>? OcclusionOctants = null);

    private sealed record CacheFile(string Path, long Length);
    private sealed record MaterialFile(string Path, ReadOnlyMemory<byte> Bytes);
    private sealed record AssetSource(
        AssetId Id,
        AssetKind Kind,
        uint CanonicalFormatVersion,
        long Size,
        string? Path,
        string? Error);
    private sealed record BuiltAsset(AssetId Id, AssetKind Kind, PackedFilePackage? Package, string? Error);
}
