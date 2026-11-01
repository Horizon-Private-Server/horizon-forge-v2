using System.Numerics;
using System.Security.Cryptography;
using Forge.Host.Domain;
using Forge.Host.Domain.Occlusion;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.Occlusion;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.UYA;

internal static class UyaOcclusionCoveragePreviewService
{
    internal static Task<EditorOcclusionCoverageEvaluation> EvaluateAsync(
        ForgeProjectWorkspace projection, string? catalogRoot, string sourcePath,
        Func<IsoProgress, ValueTask>? progress, CancellationToken token) =>
        EvaluateCoreAsync(projection, catalogRoot, sourcePath, null, progress, token);

    internal static Task<EditorOcclusionCoverageEvaluation> EvaluatePackedAsync(
        ForgeProjectWorkspace projection, byte[] packedScene, Func<IsoProgress, ValueTask>? progress, CancellationToken token) =>
        EvaluateCoreAsync(projection, null, "", packedScene, progress, token);

    private static async Task<EditorOcclusionCoverageEvaluation> EvaluateCoreAsync(
        ForgeProjectWorkspace projection, string? catalogRoot, string sourcePath, byte[]? packedScene,
        Func<IsoProgress, ValueTask>? progress, CancellationToken token)
    {
        if (projection.Manifest.Target is not { Game: "UYA", Region: "NTSC-U", Revision: "1.00" })
            throw new NotSupportedException("Coverage preview currently supports UYA NTSC-U 1.00.");
        var state = projection.Content.OcclusionCoverage; OcclusionCoverageRecipe.Validate(state);
        var shapes = state.Sources.Where(s => s.Enabled && !OcclusionCoverageRecipe.HasGraph(s)).ToArray();
        var graphs = state.Sources.Where(s => s.Enabled && OcclusionCoverageRecipe.HasGraph(s)).ToArray();
        foreach (var graph in graphs) if (graph.Graph!.Markers.Count == 0)
            throw new InvalidDataException($"Graph '{graph.Name}' ({graph.Id}) has no seeds. Add seeds in reachable camera areas or disable its contribution.");
        if (graphs.Length > 0 && packedScene is null && (string.IsNullOrWhiteSpace(catalogRoot) || string.IsNullOrWhiteSpace(sourcePath)))
            throw new InvalidOperationException("Graph preview needs the project's asset catalog and a configured source ISO to compose the current scene. Reopen with a catalog and configure the source ISO.");
        await Report(1, 1_000); token.ThrowIfCancellationRequested();
        byte[] primaryBytes = [], sourceWad = packedScene ?? [];
        var imported = state.PrimaryEnabled ? await UyaOcclusionImportPayload.ReadAsync(projection, token) : null;
        if (packedScene is null && (graphs.Length > 0 || state.PrimaryEnabled && imported is null))
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
                throw new InvalidOperationException("Primary coverage needs a configured source ISO or imported coverage. Configure a source or disable Primary.");
            await using var iso = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            token.ThrowIfCancellationRequested();
            sourceWad = LevelArchiveReader.ExtractPrimary(GameId.UYA, iso, projection.Manifest.BaseLevel.Level);
            token.ThrowIfCancellationRequested();
        }
        if (state.PrimaryEnabled) primaryBytes = imported?.AssetGrid ?? OcclusionComposer.ReadGrid(GameId.UYA, sourceWad, token);
        var primary = state.PrimaryEnabled ? OcclusionCoverageBuilder.ReadCoordinates(GameId.UYA, primaryBytes, token) : [];
        await Report(2, 2_000);
        var sdkShapes = shapes.Select(ToSdkShape).ToArray();
        OcclusionCoveragePreview preview; EditorOcclusionCoverageGraphPreview? graphPreview = null;
        if (graphs.Length == 0)
        {
            await Report(3, 7_000);
            preview = OcclusionCoverageBuilder.PreviewShapes(GameId.UYA, primary, sdkShapes, token);
        }
        else
        {
            var input = packedScene is not null
                ? new UyaOcclusionSceneInput(packedScene, projection.CurrentFingerprint, OcclusionWorkerProtocol.Hash(packedScene))
                : await UyaOcclusionSceneSnapshot.CaptureAsync(projection, catalogRoot!, sourceWad,
                    progress is null ? null : p => progress(new(2_000 + (int)((long)p.Completed * 4_000 / Math.Max(1, p.Total)), 10_000)), token);
            var scene = OcclusionSceneBuilder.BuildCoverageGeometry(GameId.UYA, input.LevelWad, token,
                includeStickyGeometry: graphs.Any(source => source.Graph!.Sticky));
            var sdkGraphs = graphs.Select(ToSdkGraph).ToArray();
            await Report(3, 7_000);
            OcclusionGraphCoveragePreview result;
            try
            {
                result = OcclusionCoverageBuilder.PreviewGraphs(GameId.UYA, primary, sdkShapes, sdkGraphs, scene.Triangles, scene.Textures, token,
                    progress is null ? null : (done, total) => progress(new(7_000 + (int)((long)done * 2_000 / Math.Max(1, total)), 10_000)).AsTask().GetAwaiter().GetResult(),
                    scene.StickyTriangles);
            }
            catch (OcclusionCoverageSeedException error) when (error.GraphIndex >= 0 && error.GraphIndex < graphs.Length
                && error.SeedIndex >= 0 && error.SeedIndex < graphs[error.GraphIndex].Graph!.Markers.Count)
            {
                var graph = graphs[error.GraphIndex]; var seed = graph.Graph!.Markers[error.SeedIndex];
                throw new InvalidDataException($"Graph '{graph.Name}' ({graph.Id}), seed 'Seed {error.SeedIndex + 1}' ({seed.Id}) appears buried in closed opaque geometry. Move it into reachable camera space or remove it.", error);
            }
            preview = result.Coverage;
            graphPreview = new(input.SceneFingerprint, result.Rays, result.QueryTests,
                graphs.Select((source, i) => new EditorOcclusionCoverageGraphResult(source.Id, Cells(result.Graphs[i].Accepted), Cells(result.Graphs[i].Rejected))).ToArray());
        }
        var counts = shapes.Select((source, i) => (source.Id, Count: preview.SourceCellCounts[i])).ToDictionary(v => v.Id, v => v.Count);
        foreach (var graph in graphPreview?.Sources ?? []) counts.Add(graph.Id, graph.Accepted.Count);
        token.ThrowIfCancellationRequested(); await Report(4, 10_000); token.ThrowIfCancellationRequested();
        return new(AlgorithmVersion(state),
            OcclusionCoverageBuilder.GetPreviewProfile(GameId.UYA).CellSize,
            Convert.ToHexStringLower(SHA256.HashData(primaryBytes)), preview.CoordinateFingerprint, primary.Count,
            preview.CandidateTests, Cells(preview.Cells), state.Sources.Select(s => new EditorOcclusionCoverageSourceCount(s.Id, counts.GetValueOrDefault(s.Id))).ToArray(), graphPreview);

        ValueTask Report(int shapeStage, int graphStage) => progress?.Invoke(graphs.Length == 0 ? new(shapeStage, 4) : new(graphStage, 10_000)) ?? ValueTask.CompletedTask;
    }

    internal static int AlgorithmVersion(ProjectOcclusionCoverageState state) =>
        state.Sources.Any(source => source.Enabled && OcclusionCoverageRecipe.HasGraph(source))
            ? OcclusionCoverageBuilder.GetGraphPreviewProfile(GameId.UYA).AlgorithmVersion
            : OcclusionCoverageBuilder.GetPreviewProfile(GameId.UYA).AlgorithmVersion;

    private static OcclusionCoverageShape ToSdkShape(ProjectOcclusionCoverageSource source)
    {
        var p = source.Transform.Position;
        var q = source.Transform.Rotation;
        var center = new Vector3(p.X, p.Y, p.Z);
        var rotation = new Quaternion(q.X, q.Y, q.Z, q.W);
        var exclude = source.Role == ProjectOcclusionCoverageRole.Exclude;
        return source.BoxSize is { } box
            ? new OcclusionCoverageBox(center, rotation, new(box.X, box.Y, box.Z), exclude)
            : new OcclusionCoverageCylinder(center, rotation, source.Cylinder!.Radius, source.Cylinder.Height, exclude);
    }

    private static OcclusionCoverageGraph ToSdkGraph(ProjectOcclusionCoverageSource source)
    {
        var p = source.Transform.Position;
        var q = source.Transform.Rotation;
        var graph = source.Graph!;
        var size = OcclusionCoverageRecipe.Dimensions(source);
        var result = new OcclusionCoverageGraph(new(p.X, p.Y, p.Z), new(q.X, q.Y, q.Z, q.W), new(size.X, size.Y, size.Z),
            graph.Markers.Select(m => new Vector3(m.Position.X, m.Position.Y, m.Position.Z)).ToArray())
        { StickyDistance = graph.Sticky ? graph.StickyDistance ?? 12 : null };
#if FORGE_OCCLUSION_COVERAGE_GRAPH_SHAPES
        return result with { CylinderRadius = source.Cylinder?.Radius };
#else
        if (source.Cylinder is not null) throw new NotSupportedException("Cylinder seed graphs require a newer SDK build.");
        return result;
#endif
    }

    private static EditorOcclusionCoverageCell[] Cells(IReadOnlyList<OcclusionCoverageCoordinate> cells) => cells.Select(c => new EditorOcclusionCoverageCell(c.X, c.Y, c.Z)).ToArray();
}
