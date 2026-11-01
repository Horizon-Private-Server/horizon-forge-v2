using System.Text.Json;
using Forge.Host.Domain;
using Forge.Host.Domain.Occlusion;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.Occlusion;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.UYA;

/// <summary>Connects SDK coverage evaluation to the existing visibility worker and atomic bake cache.</summary>
internal static class UyaOcclusionCoverageGenerationService
{
    internal static async Task PrepareAsync(ForgeProjectWorkspace workspace, string catalogRoot, byte[] source,
        string directory, CancellationToken token, int samplePullIn, bool pixelEdgeAllowance)
    {
        Report("Composing the current scene for authored coverage.");
        var scene = await UyaOcclusionSceneSnapshot.CaptureAsync(workspace, catalogRoot, source,
            p => { Report($"Composing occlusion scene: {p.Completed:N0}/{p.Total:N0}."); return ValueTask.CompletedTask; }, token);
        await PreparePackedAsync(workspace, scene.LevelWad, directory, token, samplePullIn, pixelEdgeAllowance);
    }

    internal static async Task PreparePackedAsync(ForgeProjectWorkspace workspace, byte[] packedScene,
        string directory, CancellationToken token, int samplePullIn = 2, bool pixelEdgeAllowance = false)
    {
        Report("Generating octants from the enabled coverage sources.");
        var coverage = await UyaOcclusionCoveragePreviewService.EvaluatePackedAsync(workspace, packedScene,
            p => { Report($"Generating coverage: {p.Completed:N0}/{p.Total:N0}."); return ValueTask.CompletedTask; }, token);
        if (coverage.Cells.Count == 0)
            throw new InvalidDataException("Coverage contains no octants. Add or enlarge an include volume, enable Primary, or reduce exclusions before building occlusion.");
        var coordinates = coverage.Cells.Select(c => new OcclusionCoverageCoordinate(c.X, c.Y, c.Z)).ToArray();
        var masks = SeedMasks(coordinates);
        var generated = OcclusionComposer.ApplyCoverageToLevel(GameId.UYA, packedScene, masks, token).OutputBytes
            ?? throw new InvalidDataException("The SDK did not return a level with the generated octants.");
        await UyaOcclusionBakeService.ExportAsync(generated, directory, token, samplePullIn, pixelEdgeAllowance,
            OcclusionWorkerProtocol.Hash(packedScene));
        var identity = new OcclusionBakeCoverage(workspace.OcclusionCoverageRecipeFingerprint, workspace.OcclusionVisibilityFingerprint,
            coverage.AlgorithmVersion, CoordinateHash(coordinates), UyaOcclusionVisibilityService.ReadEntities(workspace, packedScene, token));
        OcclusionBakeStore.ValidateCoverage(identity);
        await File.WriteAllBytesAsync(Path.Combine(directory, "coverage.json"), JsonSerializer.SerializeToUtf8Bytes(identity, OcclusionWorkerProtocol.Json), token);
    }

    internal static string CoordinateHash(IReadOnlyList<OcclusionCoverageCoordinate> coordinates) =>
        OcclusionWorkerProtocol.Hash(JsonSerializer.SerializeToUtf8Bytes(coordinates, OcclusionWorkerProtocol.Json));

    internal static async Task<OcclusionBakeCache> ReadAcceptedAsync(ForgeProjectWorkspace workspace, CancellationToken token) =>
        await ReadForVisibilityAsync(workspace, token)
        ?? throw new InvalidDataException("Authored coverage has no current visibility bake. Use Build occlusion in the Occlusion dock first.");

    internal static async Task<OcclusionBakeCache?> ReadForVisibilityAsync(ForgeProjectWorkspace workspace, CancellationToken token)
    {
        var cache = await OcclusionBakeStore.ReadCoverageAsync(workspace,
            UyaOcclusionCoveragePreviewService.AlgorithmVersion(workspace.Content.OcclusionCoverage), token);
        if (cache is null) return null;
        var coordinates = OcclusionCoverageBuilder.ReadCoordinates(GameId.UYA, cache.NativeGrid, token);
        if (coordinates.Count != cache.Octants || CoordinateHash(coordinates) != cache.Coverage!.CoordinateHash
            || OcclusionWorkerProtocol.Hash(OcclusionComposer.CreateGrid(GameId.UYA, SeedMasks(coordinates), token)) != cache.OriginalGridHash)
            throw new InvalidDataException("Generated coverage coordinates do not match their accepted identity. Build occlusion again.");
        return cache;
    }

    private static Dictionary<(int X, int Y, int Z), byte[]> SeedMasks(IReadOnlyList<OcclusionCoverageCoordinate> coordinates)
    {
        // The native mask selector always enables bit 1023, including cells with no source counterpart.
        var reserved = new byte[128]; reserved[127] = 128;
        return coordinates.ToDictionary(c => (c.X, c.Y, c.Z), _ => reserved);
    }

    private static void Report(string message) => Console.WriteLine(JsonSerializer.Serialize(new { completed = 0, total = 1, message }));
}
