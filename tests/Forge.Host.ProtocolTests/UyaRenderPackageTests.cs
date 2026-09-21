using Forge.Host.Domain;
using RatchetPs2.Core.Wad.Models;

internal static class UyaRenderPackageTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-render-package-{Guid.NewGuid():N}");
        var cache = Path.Combine(root, "cache");
        var projectPath = Path.Combine(root, "project");
        var catalogPath = Path.Combine(root, "catalog");
        var fingerprint = new string('a', 32);
        await ForgeProjectWorkspace.CreateAsync(
            projectPath,
            "Render test",
            new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
            new("UYA", "NTSC-U", "1.00", 3, fingerprint, EntityVersion: ProjectSchema.CurrentBaseEntityVersion),
            []);
        var request = new UyaRenderPackageRequest("", cache, fingerprint, 3, projectPath, catalogPath);
        const string sdkRevision = "test-sdk";
        try
        {
            var package = PackedFilePackageBuilder.Pack([
                new("assets/tfrag/tfrag.gltf", "{}"u8.ToArray(), "model/gltf+json"),
                new("assets/tfrag/tfrag.buffer.bin", [1, 2, 3], "application/octet-stream"),
            ]);
            var written = await UyaRenderPackageService.MaterializeAsync(request, sdkRevision, package);
            Equal(false, written.CacheHit, "first terrain cache write");
            Equal(true, File.Exists(Path.Combine(written.RootPath, "assets", "tfrag", "tfrag.gltf")), "terrain cache file");
            Equal("assets/tfrag/tfrag.gltf", written.TerrainPaths.Single(), "terrain route");

            var cached = await UyaRenderPackageService.PrepareAsync(request, sdkRevision);
            Equal(true, cached.CacheHit, "terrain cache hit without source ISO");
            Equal(written.CacheKey, cached.CacheKey, "stable terrain cache key");

            var traversal = PackedFilePackageBuilder.Pack([
                new("../escape.gltf", "{}"u8.ToArray(), "model/gltf+json"),
            ]);
            await ThrowsAsync<InvalidDataException>(() => UyaRenderPackageService.MaterializeAsync(
                request with { Fingerprint = new string('b', 32) }, sdkRevision, traversal));

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var cancelled = request with { Fingerprint = new string('c', 32) };
            await ThrowsAsync<OperationCanceledException>(() => UyaRenderPackageService.MaterializeAsync(
                cancelled, sdkRevision, package, cancellationToken: cancellation.Token));
            Equal(false, Directory.Exists(Path.Combine(root,
                UyaRenderPackageService.CreateCacheKey(cancelled, sdkRevision))), "cancelled cache is absent");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }

    private static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected {expected}, got {actual}");
    }
}
