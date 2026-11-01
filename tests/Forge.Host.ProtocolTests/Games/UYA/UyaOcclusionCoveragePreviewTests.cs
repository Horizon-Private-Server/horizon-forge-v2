#if FORGE_OCCLUSION_COVERAGE_PREVIEW
using System.Security.Cryptography;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Forge.Host.Domain;
using Forge.Host.Games.UYA;
using RatchetPs2.Core.Games;
using RatchetPs2.Sdk;

namespace Forge.Host.ProtocolTests.Games.UYA;

internal static class UyaOcclusionCoveragePreviewTests
{
    internal static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-uya-coverage-preview-{Guid.NewGuid():N}");
        try
        {
            var workspace = await ForgeProjectWorkspace.CreateAsync(root, "Coverage", new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
                new("UYA", "NTSC-U", "1.00", 3, new string('a', 32)), []);
            var grid = OcclusionComposer.CreateGrid(GameId.UYA, new Dictionary<(int, int, int), byte[]> { [(2, 2, 2)] = new byte[128] });
            var attachment = await workspace.AttachAssetAsync(AssetKind.World, 1, UyaOcclusionImportPayload.Encode(new([1], grid, [2])));
            workspace.RestoreState(workspace.CaptureState() with { Content = workspace.Content with
            {
                Occlusion = new(1, 5, new(attachment.Id, attachment.Kind)),
            } });
            var exclude = new ProjectOcclusionCoverageSource(EntityId.New(), "Exclude Primary", true, ProjectOcclusionCoverageRole.Exclude,
                ProjectTransform.Identity with { Position = new(10, 10, 10) }, BoxSize: new(4.1f, 4.1f, 4.1f));
            var cylinder = new ProjectOcclusionCoverageSource(EntityId.New(), "Island", true, ProjectOcclusionCoverageRole.Include,
                ProjectTransform.Identity with { Position = new(82, 82, 82) }, Cylinder: new(1, 1));
            var graph = new ProjectOcclusionCoverageSource(EntityId.New(), "Disabled graph", false, ProjectOcclusionCoverageRole.Include,
                ProjectTransform.Identity, Graph: new(new(8, 8, 8), []));
            workspace.SetOcclusionCoverage(new(1, true, [exclude, cylinder, graph]));
            var before = workspace.CurrentFingerprint; var progress = new List<IsoProgress>();
            var result = await UyaOcclusionCoveragePreviewService.EvaluateAsync(workspace.CreateProjection(), null, "missing-source.iso",
                value => { progress.Add(value); return ValueTask.CompletedTask; }, CancellationToken.None);
            Check(result.PrimaryCells == 1 && result.Cells.SequenceEqual(new[] { new EditorOcclusionCoverageCell(20, 20, 20) }),
                "Imported Primary and unsaved shape edits must compose without opening the ISO.");
            Check(result.PrimaryFingerprint == Convert.ToHexStringLower(SHA256.HashData(grid))
                && result.Sources.Select(s => s.Cells).SequenceEqual(new[] { 1, 1, 0 }) && result.CandidateTests == 4,
                "Preview must report actual Primary identity, per-source contributions and deduplicated work.");
            Check(progress.Count == 4 && progress[^1] == new IsoProgress(4, 4) && workspace.CurrentFingerprint == before,
                "Progress must complete without mutating project authority.");
            using var cancellation = new CancellationTokenSource();
            var baseProjection = workspace.CreateProjection(workspace.Content with { Occlusion = null });
            await ThrowsAsync<OperationCanceledException>(() => UyaOcclusionCoveragePreviewService.EvaluateAsync(
                baseProjection, null, "missing-source.iso", _ => { cancellation.Cancel(); return ValueTask.CompletedTask; }, cancellation.Token));
            workspace.SetPrimaryOcclusionCoverageEnabled(false);
            result = await UyaOcclusionCoveragePreviewService.EvaluateAsync(workspace.CreateProjection(), null, "", null, CancellationToken.None);
            Check(result.PrimaryCells == 0 && result.PrimaryFingerprint == Convert.ToHexStringLower(SHA256.HashData([])),
                "Disabled Primary must not require source files.");
            workspace.SetOcclusionCoverage(new(1, false, [graph with { Enabled = true }]));
            await ThrowsAsync<InvalidDataException>(() => UyaOcclusionCoveragePreviewService.EvaluateAsync(workspace.CreateProjection(), null, "", null, CancellationToken.None));
            await VerifyGraphAsync(Path.Combine(root, "graph-project"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static async Task VerifyGraphAsync(string root)
    {
        var catalog = await AssetCatalogStore.OpenAsync(Path.Combine(root, "catalog"));
        foreach (var (kind, id) in new[] { (AssetKind.Moby, 100), (AssetKind.Tie, 200), (AssetKind.Shrub, 300) })
            await catalog.PutAsync(kind, UyaAssetImportService.CanonicalFormatVersion, UyaProjectTests.CanonicalAsset(kind, 0x22),
                new("fixture", new("UYA", "NTSC-U", "1.00", "level03", "level_wad/assets/asset_wad.bin", 0, new string('a', 32)),
                    [$"{kind.ToString().ToLowerInvariant()}:{id}"], ["vanilla", "game:UYA", "level:03"]));
        var iso = UyaProjectTests.CreateIso(); var isoPath = Path.Combine(root, "fixture.iso");
        await File.WriteAllBytesAsync(isoPath, iso);
        var projectRoot = Path.Combine(root, "project");
        await UyaProjectService.CreateValidatedAsync(new MemoryStream(iso, writable: false), catalog,
            new("fixture.iso", catalog.RootPath, projectRoot, "Graph preview", new string('a', 32), "1.00", 3, true));
        var workspace = await ForgeProjectWorkspace.OpenAsync(projectRoot);
        workspace.RemoveEntities(workspace.Content.Entities.Where(e => e.Layer is "ties" or "shrubs" or "tfrags").Select(e => e.EntityId).ToArray());
        var graph = new ProjectOcclusionCoverageSource(EntityId.New(), "Camera room", true, ProjectOcclusionCoverageRole.Include,
            ProjectTransform.Identity with { Position = new(10, 10, 10) }, Graph: new(new(1, 1, 1), [new(EntityId.New(), "Entrance", new(0, 0, 0))]));
        workspace.SetOcclusionCoverage(new(1, false, [graph]));
        var before = workspace.CurrentFingerprint;
        var original = await File.ReadAllBytesAsync(workspace.ContentFilePath);
        var result = await UyaOcclusionCoveragePreviewService.EvaluateAsync(workspace.CreateProjection(), catalog.RootPath, isoPath, null, default);
        Check(result.AlgorithmVersion == 3 && result.GraphPreview is { } details && details.Sources.Single().Accepted.Count == 1
            && details.Sources.Single().Rejected.Count == 0 && details.Sources.Single().Id == graph.Id
            && details.SceneFingerprint.Length == 64 && result.Cells.Single() == new EditorOcclusionCoverageCell(2, 2, 2),
            "The actual host must compose and evaluate current-scene graph coverage without native-group requirements.");
#if FORGE_OCCLUSION_COVERAGE_GRAPH_SHAPES
        var cylindrical = graph with { Transform = ProjectTransform.Identity with { Position = new(64, 64, 64) },
            Cylinder = new(12, 16), Graph = graph.Graph! with { Size = null } };
        workspace.SetOcclusionCoverage(new(1, false, [cylindrical]));
        var circular = await UyaOcclusionCoveragePreviewService.EvaluateAsync(workspace.CreateProjection(), catalog.RootPath, isoPath, null, default);
        var expected = OcclusionCoverageBuilder.PreviewShapes(GameId.UYA, [],
            [new RatchetPs2.Core.Occlusion.OcclusionCoverageCylinder(new(64), System.Numerics.Quaternion.Identity, 12, 16)]);
        Check(circular.Cells.Select(c => (c.X, c.Y, c.Z)).SequenceEqual(expected.Cells.Select(c => (c.X, c.Y, c.Z))),
            "Host graph preview uses the owning cylinder boundary instead of its bounding box.");
        workspace.SetOcclusionCoverage(new(1, false, [cylindrical with { Graph = cylindrical.Graph! with { Disabled = true, Markers = [] } }]));
        var plain = await UyaOcclusionCoveragePreviewService.EvaluateAsync(workspace.CreateProjection(), null, "missing.iso", null, default);
        Check(plain.GraphPreview is null && plain.Cells.SequenceEqual(circular.Cells), "Turning off a seed graph fills its shape without requiring scene queries or seeds.");
        workspace.SetOcclusionCoverage(new(1, false, [graph]));
#endif
        var model = await UyaCustomTieProjectService.ImportAsync(workspace, UyaCustomTieProjectTests.Request(0));
        var classId = workspace.Content.CustomClasses.Allocations.Single().ClassId;
        var entity = await UyaAssetPlacementService.CreateAsync(workspace, catalog.RootPath,
            new(model.Canonical.Id, AssetKind.Tie, classId, ProjectTransform.Identity with { Position = new(16, 10, 10) }), default);
        workspace.AddEntity(entity);
        var changed = await UyaOcclusionCoveragePreviewService.EvaluateAsync(workspace.CreateProjection(), catalog.RootPath, isoPath, null, default);
        Check(changed.GraphPreview!.SceneFingerprint != result.GraphPreview!.SceneFingerprint && changed.GraphPreview.QueryTests > 0,
            "Unsaved custom native geometry must enter coverage queries and invalidate scene identity.");
        var sourceWad = LevelArchiveReader.ExtractPrimary(GameId.UYA, new MemoryStream(iso, writable: false), 3);
        var input = await UyaOcclusionSceneSnapshot.CaptureAsync(workspace.CreateProjection(), catalog.RootPath, sourceWad, null, default);
        var geometry = OcclusionSceneBuilder.BuildCoverageGeometry(GameId.UYA, input.LevelWad);
        Check(geometry.Triangles.Count == 2 && geometry.Triangles.All(t => t.Group == -1),
            "Coverage geometry must retain both sides of the custom triangle without assigning visibility groups.");
        var stickyGeometry = OcclusionSceneBuilder.BuildCoverageGeometry(GameId.UYA, input.LevelWad, includeStickyGeometry: true);
        Check(stickyGeometry.StickyTriangles is { Count: 2 }
            && stickyGeometry.StickyTriangles.SequenceEqual(geometry.Triangles)
            && ReferenceEquals(stickyGeometry.StickyTriangles, stickyGeometry.Triangles),
            "Sticky geometry includes the current unsaved TIE and shares the calibrated render faces.");
        var stickySource = graph with { Graph = graph.Graph! with { Sticky = true, StickyDistance = 0 } };
        var stickyProjection = workspace.CreateProjection(workspace.Content with { OcclusionCoverage = new(1, false, [stickySource]) });
        var stickyResult = await UyaOcclusionCoveragePreviewService.EvaluatePackedAsync(stickyProjection, input.LevelWad, null, default);
        Check(stickyResult.Cells.Count == 0 && stickyResult.GraphPreview!.Sources.Single().Rejected.Count == 1,
            "Host preview applies Sticky to authored graphs and rejects a tiny region far from the TIE.");
        await ThrowsAsync<InvalidDataException>(() => Task.Run(() => OcclusionSceneBuilder.Build(GameId.UYA, input.LevelWad)));
        var packedFiles = RatchetPs2.Games.UYA.Level.UyaLevelWadUnpacker.Unpack(input.LevelWad).Files;
        var noGridHeader = packedFiles.Single(f => f.Path == "assets/asset_header.bin").Bytes.ToArray();
        noGridHeader.AsSpan(0x0c, 4).Clear();
        var noGrid = LevelArchiveBuilder.Build(GameId.UYA, input.LevelWad,
            new Dictionary<string, ReadOnlyMemory<byte>> { ["assets/asset_header.bin"] = noGridHeader });
        Check(noGrid.Succeeded && OcclusionSceneBuilder.BuildCoverageGeometry(GameId.UYA, noGrid.OutputBytes!).Triangles.Count == 2,
            "Coverage extraction must not require a native octant grid.");
        Check((await File.ReadAllBytesAsync(workspace.ContentFilePath)).SequenceEqual(original) && workspace.CurrentFingerprint != before,
            "Graph evaluation must include unsaved edits without saving the original project.");
        using var cancellation = new CancellationTokenSource();
        await ThrowsAsync<OperationCanceledException>(() => UyaOcclusionCoveragePreviewService.EvaluateAsync(workspace.CreateProjection(), catalog.RootPath, isoPath,
            p => { if (p.Completed >= 2_000) cancellation.Cancel(); return ValueTask.CompletedTask; }, cancellation.Token));
        var cube = await UyaCustomTieProjectService.ImportAsync(workspace, CubeRequest());
        var cubeClass = workspace.Content.CustomClasses.Allocations.Single(c => c.Asset == cube.Canonical).ClassId;
        workspace.AddEntity(await UyaAssetPlacementService.CreateAsync(workspace, catalog.RootPath,
            new(cube.Canonical.Id, AssetKind.Tie, cubeClass, ProjectTransform.Identity with { Position = new(10, 10, 10) }), default));
        var buried = graph.Graph!.Markers[0] with { Name = "Seed 2 copy copy" };
        var outside = new ProjectOcclusionCoverageMarker(EntityId.New(), "Seed 1", new(-2, 0, 0));
        foreach (var markers in new[] { new[] { outside, buried }, new[] { buried } })
        {
            workspace.SetOcclusionCoverage(new(1, false, [graph with { Graph = graph.Graph with { Size = new(4, 4, 4), Markers = markers } }]));
            try
            {
                await UyaOcclusionCoveragePreviewService.EvaluateAsync(workspace.CreateProjection(), catalog.RootPath, isoPath, null, default);
                throw new Exception("Buried graph seed was accepted.");
            }
            catch (InvalidDataException error)
            {
                Check(error.Message.Contains(graph.Name) && error.Message.Contains(graph.Id.ToString())
                    && error.Message.Contains($"seed 'Seed {markers.Length}' ({buried.Id})") && !error.Message.Contains(buried.Name),
                    "Buried-seed diagnostics must use the current list index, including after deleting an earlier seed, and retain stable IDs.");
            }
        }
    }

    private static ProjectCustomModelImportRequest CubeRequest()
    {
        float[] positions = [-1,-1,-1, 1,-1,-1, 1,1,-1, -1,1,-1, -1,-1,1, 1,-1,1, 1,1,1, -1,1,1];
        ushort[] indices = [0,3,2, 0,2,1, 4,5,6, 4,6,7, 0,1,5, 0,5,4, 3,7,6, 3,6,2, 0,4,7, 0,7,3, 1,2,6, 1,6,5];
        var data = new byte[positions.Length * 4 + indices.Length * 2];
        for (var i = 0; i < positions.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(i * 4), positions[i]);
        for (var i = 0; i < indices.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(positions.Length * 4 + i * 2), indices[i]);
        var json = JsonSerializer.Serialize(new
        {
            asset = new { version = "2.0" }, buffers = new[] { new { uri = "data:application/octet-stream;base64," + Convert.ToBase64String(data), byteLength = data.Length } },
            bufferViews = new[] { new { buffer = 0, byteOffset = 0, byteLength = positions.Length * 4 }, new { buffer = 0, byteOffset = positions.Length * 4, byteLength = indices.Length * 2 } },
            accessors = new[] { new { bufferView = 0, componentType = 5126, count = 8, type = "VEC3" }, new { bufferView = 1, componentType = 5123, count = indices.Length, type = "SCALAR" } },
            materials = new[] { new { doubleSided = false } }, meshes = new[] { new { primitives = new[] { new { attributes = new { POSITION = 0 }, indices = 1, material = 0, mode = 4 } } } },
            nodes = new[] { new { mesh = 0 } }, scenes = new[] { new { nodes = new[] { 0 } } }, scene = 0,
        });
        return new("closed-cube.gltf", Encoding.UTF8.GetBytes(json), [], true);
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
#endif
