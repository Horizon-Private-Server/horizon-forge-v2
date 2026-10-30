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
        var path = CachePath(projectRoot);
        if (!File.Exists(path)) return null;
        var raw = await ReadBoundedAsync(path, 32 * 1024 * 1024, token);
        var cache = JsonSerializer.Deserialize<OcclusionBakeCache>(raw, OcclusionWorkerProtocol.Json)
            ?? throw new InvalidDataException("Generated occlusion cache is empty; rebuild occlusion.");
        Validate(cache, allowPreviousProfile: true);
        if (cache.OriginalGridHash != originalGridHash)
            throw new InvalidDataException("Generated occlusion is out of date for the staged scene. Rebuild occlusion before building this scene.");
        return cache;
    }
    public static async Task CommitAsync(string projectRoot, OcclusionBakeCache cache, CancellationToken token)
    {
        Validate(cache); token.ThrowIfCancellationRequested();
        await ForgeProjectPersistence.WriteFileSafelyAsync(CachePath(projectRoot), JsonSerializer.SerializeToUtf8Bytes(cache, OcclusionWorkerProtocol.Json), token);
    }
    private static void Validate(OcclusionBakeCache c, bool allowPreviousProfile = false)
    {
        var validProfile = c.Profile == OcclusionWorkerProtocol.Profile
            || allowPreviousProfile && c.Profile is "raster1024-lod0-uniform-samples-no-feather-v20"
                or "raster1024-lod0-inward-local-corners-no-feather-v19";
        if (c.SchemaVersion != 1 || !validProfile || c.SamplePullIn is < 0 or > 5 || c.NativeGrid is null || c.NativeGrid.Length is < 1 or > 16 * 1024 * 1024
            || !BakeSchema.IsFingerprint(c.InputHash ?? "") || !BakeSchema.IsFingerprint(c.OriginalGridHash ?? "")
            || c.NativeGridHash != OcclusionWorkerProtocol.Hash(c.NativeGrid) || c.Octants is < 1 or > OcclusionBakeLimits.MaximumCells
            || !double.IsFinite(c.WallMs) || c.WallMs < 0 || c.GpuMs is { } gpu && (!double.IsFinite(gpu) || gpu < 0))
            throw new InvalidDataException("Generated occlusion cache is invalid; rebuild occlusion.");
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
