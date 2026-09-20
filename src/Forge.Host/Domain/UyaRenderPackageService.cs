using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RatchetPs2.Core.Wad.Models;
using RatchetPs2.Games.DL.Level;
using RatchetPs2.Games.UYA.Level;
using RatchetPs2.Sdk;

namespace Forge.Host.Domain;

public static class UyaRenderPackageService
{
    public const int SchemaVersion = 1;
    private const string MarkerName = ".forge-render-package.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<UyaRenderPackageResult> PrepareAsync(
        UyaRenderPackageRequest request,
        string sdkRevision,
        Func<IsoProgress, ValueTask>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Validate(request, sdkRevision);
        var cacheKey = CreateCacheKey(request, sdkRevision);
        var target = Path.Combine(Path.GetFullPath(request.CacheRootPath), cacheKey);
        var cached = await TryOpenAsync(target, request, sdkRevision, cancellationToken);
        if (cached is not null)
        {
            await ReportAsync(progress, 10_000, 10_000);
            return cached with { CacheHit = true };
        }
        if (string.IsNullOrWhiteSpace(request.SourceIsoPath) || !File.Exists(request.SourceIsoPath))
            throw new IOException("The terrain cache is missing and the clean UYA ISO is unavailable. Repair it through Forge → Setup.");

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
            () => UyaLooseLevelWadExtractor.ExtractPrimary(iso, request.Level).Bytes,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await ReportAsync(progress, 3_000, 10_000);
        var package = await Task.Run(
            () => UyaFrontendMapPackageBuilder.BuildLevelWadPart(levelWad, DlLevelAssetGroup.Terrain),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await ReportAsync(progress, 7_000, 10_000);
        return await MaterializeAsync(request, sdkRevision, package, progress, cancellationToken);
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
        var cacheRoot = Path.GetFullPath(request.CacheRootPath);
        var cacheKey = CreateCacheKey(request, sdkRevision);
        var target = Path.Combine(cacheRoot, cacheKey);
        var cached = await TryOpenAsync(target, request, sdkRevision, cancellationToken);
        if (cached is not null) return cached with { CacheHit = true };

        var entries = ValidateEntries(package);
        var terrainPaths = entries.Select(entry => entry.Path)
            .Where(path => (path.StartsWith("tfrag/", StringComparison.Ordinal)
                    || path.Contains("/tfrag/", StringComparison.Ordinal))
                && path.EndsWith("/tfrag.gltf", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (terrainPaths.Length == 0) throw new InvalidDataException("The SDK render package contains no tfrag glTF files.");

        Directory.CreateDirectory(cacheRoot);
        var partial = Path.Combine(cacheRoot, $".{cacheKey}.{Guid.NewGuid():N}.partial");
        Directory.CreateDirectory(partial);
        try
        {
            long written = 0;
            var total = Math.Max(1, entries.Sum(entry => (long)entry.Length));
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = ResolveEntryPath(partial, entry.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var output = new FileStream(
                    destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await output.WriteAsync(package.PackedBytes.AsMemory(entry.Offset, entry.Length), cancellationToken);
                await output.FlushAsync(cancellationToken);
                written += entry.Length;
                await ReportAsync(progress, 7_000 + written * 2_900 / total, 10_000);
            }

            var marker = new CacheMarker(
                SchemaVersion,
                cacheKey,
                request.Fingerprint.ToLowerInvariant(),
                request.Level,
                sdkRevision,
                entries.Select(entry => new CacheFile(entry.Path, entry.Length)).ToArray(),
                terrainPaths);
            await File.WriteAllBytesAsync(
                Path.Combine(partial, MarkerName),
                JsonSerializer.SerializeToUtf8Bytes(marker, JsonOptions),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(partial, target);
            await ReportAsync(progress, 10_000, 10_000);
            return new(target, cacheKey, terrainPaths, false);
        }
        catch
        {
            if (Directory.Exists(partial)) Directory.Delete(partial, recursive: true);
            throw;
        }
    }

    public static string CreateCacheKey(UyaRenderPackageRequest request, string sdkRevision)
    {
        Validate(request, sdkRevision);
        var revisionKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sdkRevision)))
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
            || marker.TerrainPaths.Count == 0)
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
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
        return new(root, marker.CacheKey, marker.TerrainPaths, true);
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
        IReadOnlyList<string> TerrainPaths);

    private sealed record CacheFile(string Path, long Length);
}
