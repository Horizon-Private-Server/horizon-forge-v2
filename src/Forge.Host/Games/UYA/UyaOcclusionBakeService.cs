using System.Numerics;
using System.Text.Json;
using Forge.Host.Domain;
using Forge.Host.Domain.Occlusion;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.Occlusion;
using RatchetPs2.Core.Wad.Models;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.UYA;

internal static class UyaOcclusionBakeService
{
    internal static async Task PrepareAsync(string projectRoot, string catalogRoot, string sourceIso, string directory,
        string hostVersion, string sdkRevision, CancellationToken token, int samplePullIn = 2, bool pixelEdgeAllowance = false)
    {
        var workspace = await ForgeProjectWorkspace.OpenAsync(projectRoot, token);
        if (workspace.Manifest.Target is not { Game: "UYA", Region: "NTSC-U", Revision: "1.00" })
            throw new NotSupportedException("Occlusion scene export currently supports UYA NTSC-U 1.00.");
        Report(0, 1, "Baking the current project scene for occlusion.");
        var catalog = await AssetCatalogStore.OpenReadOnlyAsync(catalogRoot, token);
        var context = new BakeFingerprintContext(workspace.Manifest.Target, $"ratchet-sdk:{sdkRevision}", $"forge-host:{hostVersion}");
        var bake = await UyaBakeService.BakeAsync(projectRoot, catalog, context,
            progress: p => { Report(p.CompletedLayers, Math.Max(1, p.TotalLayers), p.Message); return ValueTask.CompletedTask; }, cancellationToken: token);
        if (!bake.Succeeded) throw new InvalidDataException(string.Join(" ", bake.Validation.Diagnostics.Select(d => $"{d.Cause} {d.CorrectiveAction}")));
        await using var iso = new FileStream(sourceIso, FileMode.Open, FileAccess.Read, FileShare.Read);
        var source = await Task.Run(() => LevelArchiveReader.ExtractPrimary(GameId.UYA, iso, workspace.Manifest.BaseLevel.Level), token);
        var pack = await UyaLevelPackService.PackAsync(projectRoot, catalog, source, context,
            cancellationToken: token, applyGeneratedOcclusion: false);
        if (!pack.Succeeded || pack.OutputBytes is null) throw new InvalidDataException(string.Join(" ", pack.Diagnostics.Select(d => d.Cause)));
        Report(0, 1, "Preparing LOD0 faces, texture alpha and target bounds.");
        await ExportAsync(pack.OutputBytes, directory, token, samplePullIn, pixelEdgeAllowance);
        await File.WriteAllTextAsync(Path.Combine(directory, "project-fingerprint.txt"), workspace.CurrentFingerprint, token);
    }

    internal static async Task ExportAsync(byte[] wad, string directory, CancellationToken token, int samplePullIn = 2, bool pixelEdgeAllowance = false)
    {
        var input = await Task.Run(() => OcclusionSceneBuilder.Build(GameId.UYA, wad, token), token);
        Report(0, input.Octants.Count, "Qualifying corner cameras against opaque faces.");
        var qualifier = OcclusionSceneBuilder.CreateQualifier(GameId.UYA, input, token);
        var cameras = OcclusionSamplePlanner.PlanCells(input.Octants, token, qualifier,
            (completed, total) => Report(completed, total, $"Qualifying occlusion samples: {completed:N0}/{total:N0} octants."),
            samplePullIn, input.BarrierHeights);
        var origin = cameras.Origins[0];
        var point = new Vector3(origin[0], origin[1], origin[2]);
        var rays = new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ }
            .Select(direction => (point, direction)).ToArray();
        var cpu = OcclusionSceneBuilder.TraceReference(GameId.UYA, input.Triangles, rays, input.Textures, input.Spheres, token,
            backfaceExitDistance: OcclusionWorkerProtocol.BackfaceExitDistance, barrierHeights: input.BarrierHeights);
        var checks = rays.Select((ray, i) => new OcclusionGpuCheck(origin, [ray.direction.X, ray.direction.Y, ray.direction.Z],
            Convert.ToHexStringLower(cpu[i].Geometry), Convert.ToHexStringLower(cpu[i].Mobys))).ToArray();
        await OcclusionWorkerProtocol.WriteSceneAsync(directory, OcclusionWorkerProtocol.Hash(wad),
            input.Triangles.Select((triangle, i) => new OcclusionGpuTriangle(triangle.A, triangle.B, triangle.C, triangle.Group,
                triangle.Flags, triangle.UvA, triangle.UvB, triangle.UvC, triangle.TextureIndex, triangle.WrapFlags,
                (uint)(triangle.SourceIndex >= 0 ? triangle.SourceIndex + 1 : i + 1))).ToArray(),
            input.Textures.Select(texture => new OcclusionGpuTexture(texture.Width, texture.Height, texture.Alpha)).ToArray(),
            cameras.Origins, cameras.Cells,
            input.Spheres.Select(sphere => new OcclusionGpuSphere(sphere.Center, sphere.Radius, sphere.Group)).ToArray(),
            checks, input.FallbackMask, token, samplePullIn, input.BarrierHeights, pixelEdgeAllowance);
        await File.WriteAllBytesAsync(Path.Combine(directory, "original-grid.bin"), input.OriginalGrid, token);
    }

    internal static async Task CommitAsync(string projectRoot, string directory, CancellationToken token)
    {
        var workspace = await ForgeProjectWorkspace.OpenAsync(projectRoot, token);
        var fingerprint = await File.ReadAllTextAsync(Path.Combine(directory, "project-fingerprint.txt"), token);
        if (fingerprint != workspace.CurrentFingerprint) throw new InvalidDataException("The saved project changed during occlusion generation. Rebuild occlusion again.");
        var raw = await OcclusionBakeStore.ReadBoundedAsync(Path.Combine(directory, "scene.json"), OcclusionBakeLimits.MaximumSceneBytes, token);
        var scene = JsonSerializer.Deserialize<OcclusionScene>(raw, OcclusionWorkerProtocol.Json) ?? throw new InvalidDataException("Occlusion scene is missing.");
        var result = JsonSerializer.Deserialize<OcclusionBakeResult>(await OcclusionBakeStore.ReadBoundedAsync(Path.Combine(directory, "result.json"), OcclusionBakeLimits.MaximumResultBytes, token), OcclusionWorkerProtocol.Json)
            ?? throw new InvalidDataException("Occlusion result is missing.");
        var masks = OcclusionWorkerProtocol.ValidateResult(scene, raw, result, token);
        var original = await OcclusionBakeStore.ReadBoundedAsync(Path.Combine(directory, "original-grid.bin"), 16 * 1024 * 1024, token);
        var expected = scene.Cells.Select((c, i) => (c, i)).ToDictionary(v => (v.c.X, v.c.Y, v.c.Z), v => masks.AsSpan(v.i * 128, 128).ToArray());
        Report(0, 1, "Compacting visibility masks within the source grid allocation.");
        var native = await Task.Run(() => OcclusionComposer.RebuildGrid(GameId.UYA, original, expected, token), token);
        await OcclusionBakeStore.CommitAsync(projectRoot, new(1, OcclusionWorkerProtocol.Profile, scene.InputHash,
            OcclusionWorkerProtocol.Hash(original), OcclusionWorkerProtocol.Hash(native), native, scene.Cells.Count, result.WallMs, result.GpuMs, scene.SamplePullIn, scene.PixelEdgeAllowance), token);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            completed = scene.Origins.Count,
            total = scene.Origins.Count,
            message = $"Occlusion rebuilt: {scene.Cells.Count:N0} octants in {result.WallMs / 1000:F2}s GPU worker time.",
            summary = new { octants = scene.Cells.Count, origins = scene.Origins.Count, wallMs = result.WallMs, gpuMs = result.GpuMs }
        }));
    }

    internal static async Task<LevelArchiveBuildResult?> ApplyAsync(string projectRoot, byte[] wad, CancellationToken token)
    {
        if (!OcclusionBakeStore.Exists(projectRoot)) return null;
        var original = OcclusionComposer.ReadGrid(GameId.UYA, wad, token);
        // shortcut: Hash the entire packed scene; narrow this when unrelated edits cause costly rebakes.
        var cache = await OcclusionBakeStore.ReadAsync(projectRoot, OcclusionWorkerProtocol.Hash(wad), OcclusionWorkerProtocol.Hash(original), token);
        if (cache is null) return null;
        return OcclusionComposer.ApplyToLevel(GameId.UYA, wad, cache.NativeGrid, cache.Octants, token);
    }

    private static void Report(int completed, int total, string message) => Console.WriteLine(JsonSerializer.Serialize(new { completed, total, message }));
}
