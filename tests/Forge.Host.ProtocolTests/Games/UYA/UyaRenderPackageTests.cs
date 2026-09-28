using Forge.Host.Games.UYA;
using Forge.Host.Domain;
using RatchetPs2.Core.Wad.Models;

namespace Forge.Host.ProtocolTests.Games.UYA;

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
            var sky = PackedFilePackageBuilder.Pack([
                new("assets/skybox/skybox.gltf", "{}"u8.ToArray(), "model/gltf+json"),
                new("assets/skybox/skybox.buffer.bin", [4, 5, 6], "application/octet-stream"),
                new("world/ignored.bin", [7], "application/octet-stream"),
            ]);
            var environment = new UyaRenderEnvironmentResult(57, 65, 50, 40, 50, 40, 10, 175, 255, 0);
            var written = await UyaRenderPackageService.MaterializeAsync(
                request, sdkRevision, package, sky, environment);
            Equal(false, written.CacheHit, "first terrain cache write");
            Equal(true, File.Exists(Path.Combine(written.RootPath, "assets", "tfrag", "tfrag.gltf")), "terrain cache file");
            Equal("assets/tfrag/tfrag.gltf", written.TerrainPaths.Single(), "terrain route");
            Equal("assets/skybox/skybox.gltf", written.SkyPath, "sky route");
            Equal(environment, written.Environment, "environment metadata");
            Equal(false, File.Exists(Path.Combine(written.RootPath, "world", "ignored.bin")), "unrelated common file omitted");

            var cached = await UyaRenderPackageService.PrepareAsync(request, sdkRevision);
            Equal(true, cached.CacheHit, "terrain cache hit without source ISO");
            Equal(written.CacheKey, cached.CacheKey, "stable terrain cache key");

            var previewRequest = new UyaAssetPreviewRequest(
                cache,
                catalogPath,
                AssetId.Compute(AssetKind.Tie, 2, "preview"u8),
                AssetKind.Tie,
                "UYA");
            var previewPackage = PackedFilePackageBuilder.Pack([
                new("model.gltf", "{}"u8.ToArray(), "model/gltf+json"),
                new("model.buffer.bin", [8, 9, 10], "application/octet-stream"),
            ]);
            var preview = await UyaAssetPreviewService.MaterializeAsync(
                previewRequest, sdkRevision, previewPackage);
            Equal(false, preview.CacheHit, "first asset preview cache write");
            Equal(true, File.Exists(Path.Combine(preview.RootPath, preview.ModelPath)), "asset preview model");

            var previewCached = await UyaAssetPreviewService.MaterializeAsync(
                previewRequest, sdkRevision, previewPackage);
            Equal(true, previewCached.CacheHit, "asset preview cache hit");
            Equal(preview.CacheKey, previewCached.CacheKey, "stable asset preview cache key");
            Equal(false,
                preview.CacheKey == UyaAssetPreviewService.CreateCacheKey(previewRequest, "changed-sdk"),
                "SDK revision changes asset preview cache key");

            await File.WriteAllTextAsync(Path.Combine(preview.RootPath, preview.ModelPath), "corrupt");
            var repaired = await UyaAssetPreviewService.MaterializeAsync(
                previewRequest, sdkRevision, previewPackage);
            Equal(false, repaired.CacheHit, "corrupt asset preview cache is replaced");
            Equal("{}", await File.ReadAllTextAsync(Path.Combine(repaired.RootPath, repaired.ModelPath)),
                "repaired asset preview model");

            var previewTraversal = PackedFilePackageBuilder.Pack([
                new("model.gltf", "{}"u8.ToArray(), "model/gltf+json"),
                new("../escape.bin", [11], "application/octet-stream"),
            ]);
            await ThrowsAsync<InvalidDataException>(() => UyaAssetPreviewService.MaterializeAsync(
                previewRequest with { AssetId = AssetId.Compute(AssetKind.Tie, 2, "traversal"u8) },
                sdkRevision,
                previewTraversal));

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

            var previewCancelled = previewRequest with {
                AssetId = AssetId.Compute(AssetKind.Tie, 2, "cancelled"u8),
            };
            await ThrowsAsync<OperationCanceledException>(() => UyaAssetPreviewService.MaterializeAsync(
                previewCancelled, sdkRevision, previewPackage, cancellation.Token));
            Equal(false, Directory.Exists(Path.Combine(cache,
                UyaAssetPreviewService.CreateCacheKey(previewCancelled, sdkRevision))),
                "cancelled asset preview cache is absent");
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
