using System.Text.Json;

namespace Forge.Host.Domain.Occlusion;

public static class OcclusionBakeStore
{
    private const string RelativePath = "cache/occlusion/generated.json";
    public static bool Exists(string projectRoot) => File.Exists(CachePath(projectRoot));
    public static async Task<OcclusionBakeCache?> ReadAsync(string projectRoot, string inputHash, string originalGridHash, CancellationToken token)
    {
        var cache = await ReadForPreviewAsync(projectRoot, originalGridHash, token);
        if (cache is not null) Validate(cache);
        if (cache is not null && cache.InputHash != inputHash)
            throw new InvalidDataException("Generated occlusion is out of date for the staged scene. Rebuild occlusion before building this scene.");
        return cache;
    }

    public static async Task<OcclusionBakeCache?> ReadForPreviewAsync(string projectRoot, string originalGridHash, CancellationToken token)
    {
        var cache = await ReadCacheAsync(projectRoot, token);
        if (cache?.Coverage is not null) return null;
        if (cache is not null && cache.OriginalGridHash != originalGridHash)
            throw new InvalidDataException("Generated occlusion is out of date for the staged scene. Rebuild occlusion before building this scene.");
        return cache;
    }

    public static async Task<OcclusionBakeCache?> ReadCoverageAsync(ForgeProjectWorkspace workspace, int algorithmVersion, CancellationToken token)
    {
        var cache = await ReadCacheAsync(workspace.RootPath, token);
        if (cache is null) return null;
        Validate(cache);
        return cache.Coverage is { } coverage && HasCurrentCoverageIdentity(workspace, coverage, algorithmVersion) ? cache : null;
    }

    public static void ValidateCoverageIdentity(ForgeProjectWorkspace workspace, OcclusionBakeCoverage coverage, int algorithmVersion)
    {
        ValidateCoverage(coverage);
        if (!HasCurrentCoverageIdentity(workspace, coverage, algorithmVersion))
            throw new InvalidDataException("The scene or coverage sources changed. Build occlusion again before building the map.");
    }

    private static bool HasCurrentCoverageIdentity(ForgeProjectWorkspace workspace, OcclusionBakeCoverage coverage, int algorithmVersion) =>
        workspace.Content.OcclusionCoverage.RequiresGeneration && coverage.RecipeHash == workspace.OcclusionCoverageRecipeFingerprint
        && coverage.ProjectHash == workspace.OcclusionVisibilityFingerprint && coverage.AlgorithmVersion == algorithmVersion;

    private static async Task<OcclusionBakeCache?> ReadCacheAsync(string projectRoot, CancellationToken token)
    {
        var path = CachePath(projectRoot);
        if (!File.Exists(path)) return null;
        var raw = await ReadBoundedAsync(path, 32 * 1024 * 1024, token);
        OcclusionBakeCache cache;
        try { cache = JsonSerializer.Deserialize<OcclusionBakeCache>(raw, OcclusionWorkerProtocol.Json)
            ?? throw new InvalidDataException("Generated occlusion cache is empty; rebuild occlusion."); }
        catch (JsonException error) { throw new InvalidDataException("Generated occlusion cache is malformed; build occlusion again.", error); }
        Validate(cache, allowPreviousProfile: true);
        return cache;
    }
    public static async Task CommitAsync(string projectRoot, OcclusionBakeCache cache, CancellationToken token)
    {
        Validate(cache); token.ThrowIfCancellationRequested();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(cache, OcclusionWorkerProtocol.Json);
        if (bytes.Length > 32 * 1024 * 1024) throw new InvalidDataException("Generated occlusion cache exceeds its resource budget.");
        await ForgeProjectPersistence.WriteFileSafelyAsync(CachePath(projectRoot), bytes, token);
    }
    private static void Validate(OcclusionBakeCache c, bool allowPreviousProfile = false)
    {
        if (c.Coverage is { } coverage) ValidateCoverage(coverage);
        var validProfile = c.Profile == OcclusionWorkerProtocol.Profile
            || allowPreviousProfile && c.Profile is "raster1024-lod0-uniform-samples-no-feather-v20"
                or "raster1024-lod0-inward-local-corners-no-feather-v19";
        if (c.SchemaVersion != 1 || !validProfile || c.SamplePullIn is < 0 or > 5 || c.NativeGrid is null || c.NativeGrid.Length is < 1 or > 16 * 1024 * 1024
            || !BakeSchema.IsFingerprint(c.InputHash ?? "") || !BakeSchema.IsFingerprint(c.OriginalGridHash ?? "")
            || c.NativeGridHash != OcclusionWorkerProtocol.Hash(c.NativeGrid) || c.Octants is < 1 or > OcclusionBakeLimits.MaximumCells
            || !double.IsFinite(c.WallMs) || c.WallMs < 0 || c.GpuMs is { } gpu && (!double.IsFinite(gpu) || gpu < 0))
            throw new InvalidDataException("Generated occlusion cache is invalid; rebuild occlusion.");
    }
    public static void ValidateCoverage(OcclusionBakeCoverage c)
    {
        if (!BakeSchema.IsFingerprint(c.RecipeHash ?? "") || !BakeSchema.IsFingerprint(c.ProjectHash ?? "")
            || !BakeSchema.IsFingerprint(c.CoordinateHash ?? "") || c.AlgorithmVersion < 1 || c.Entities is null
            || c.Entities.Count > 100_000 || c.Entities.Any(e => e is null || !Guid.TryParse(e.Id, out _)
                || e.Groups is null || e.Groups.Count is < 1 or > 1023 || e.Groups.Any(g => g is < 0 or >= 1023))
            || c.Entities.Select(e => e.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != c.Entities.Count)
            throw new InvalidDataException("Generated coverage identity or object mappings are invalid; build occlusion again.");
        if (c.Entities.Sum(e => (long)e.Groups.Count) > 1_000_000)
            throw new InvalidDataException("Generated coverage object mappings exceed their resource budget.");
    }
    private static string CachePath(string root)
    {
        var path = ForgeProjectPersistence.ResolveRelativePath(root, RelativePath);
        for (var p = path; p is not null && p != Path.GetDirectoryName(Path.GetFullPath(root)); p = Path.GetDirectoryName(p))
            if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Occlusion cache cannot use symbolic links.");
        return path;
    }
    public static async Task<byte[]> ReadBoundedAsync(string path, int maximum, CancellationToken token)
    {
        var info = new FileInfo(path);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length > maximum)
            throw new InvalidDataException("Occlusion job file is missing, oversized or a symbolic link.");
        var bytes = await File.ReadAllBytesAsync(path, token);
        if (bytes.Length > maximum) throw new InvalidDataException("Occlusion job file exceeds limit.");
        return bytes;
    }
}
