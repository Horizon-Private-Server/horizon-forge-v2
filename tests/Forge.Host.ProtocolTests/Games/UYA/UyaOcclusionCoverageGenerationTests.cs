#if FORGE_OCCLUSION_COVERAGE_GENERATION
using System.Text.Json;
using System.Buffers.Binary;
using Forge.Host.Domain;
using Forge.Host.Domain.Occlusion;
using Forge.Host.Games.UYA;
using RatchetPs2.Core.Games;
using RatchetPs2.Sdk;
using RatchetPs2.Games.UYA.Level;
using RatchetPs2.Games.UYA.Occlusion;
using RatchetPs2.Core.LevelAssets;

namespace Forge.Host.ProtocolTests.Games.UYA;

internal static class UyaOcclusionCoverageGenerationTests
{
    internal static async Task RunAsync(string? exportFixturePath = null)
    {
        var root = Directory.CreateTempSubdirectory("forge-coverage-build-tests-").FullName;
        try
        {
            var catalog = await AssetCatalogStore.OpenAsync(Path.Combine(root, "catalog"));
            foreach (var (kind, id) in new[] { (AssetKind.Moby, 100), (AssetKind.Tie, 200), (AssetKind.Shrub, 300) })
                await catalog.PutAsync(kind, UyaAssetImportService.CanonicalFormatVersion, UyaProjectTests.CanonicalAsset(kind, 0x22),
                    new("fixture", new("UYA", "NTSC-U", "1.00", "level03", "level_wad/assets/asset_wad.bin", 0, new string('a', 32)),
                        [$"{kind.ToString().ToLowerInvariant()}:{id}"], ["vanilla", "game:UYA", "level:03"]));
            var iso = UyaProjectTests.CreateIso(); var projectRoot = Path.Combine(root, "project");
            await UyaProjectService.CreateValidatedAsync(new MemoryStream(iso, writable: false), catalog,
                new("fixture.iso", catalog.RootPath, projectRoot, "Coverage build", new string('a', 32), "1.00", 3, true));
            var workspace = await ForgeProjectWorkspace.OpenAsync(projectRoot);
            workspace.RemoveEntities(workspace.Content.Entities.Where(e => e.Layer is "mobys" or "shrubs").Select(e => e.EntityId).ToArray());
            var model = await UyaCustomTieProjectService.ImportAsync(workspace, UyaCustomTieProjectTests.Request(0));
            var nativeModel = UyaCustomTieNativeCodec.Read(await workspace.ReadAttachedAssetVerifiedAsync(model.Native, ForgeProjectWorkspace.MaxAttachedAssetBytes));
            var textures = new List<FrontendAssetTexture>();
            foreach (var texture in model.Textures)
                textures.Add(new(0, await workspace.ReadAttachedAssetVerifiedAsync(texture, ForgeProjectWorkspace.MaxAttachedAssetBytes)));
            var asset = await workspace.AttachAssetAsync(AssetKind.Tie, UyaAssetImportService.CanonicalFormatVersion,
                UyaCanonicalAssetCodec.Encode(nativeModel.DefinitionBytes, nativeModel.ModelBytes, textures));
            workspace.SetAssetOverride(workspace.Content.Entities.Single(e => e.Layer == "ties").Asset!, new(asset.Id, asset.Kind));
            var source = LevelArchiveReader.ExtractPrimary(GameId.UYA, new MemoryStream(iso, writable: false), 3);
            var sourceAssets = UyaLevelWadRenderPackageBuilder.ReadAssetSourceFiles(UyaLevelWadUnpacker.Unpack(source).Files);
            BinaryPrimitives.WriteInt32LittleEndian(sourceAssets.HeaderBytes.AsSpan(0x7c), sourceAssets.AssetWadBytes.Length);
            source = LevelArchiveBuilder.Build(GameId.UYA, source, new Dictionary<string, ReadOnlyMemory<byte>>
                { ["assets/asset_header.bin"] = sourceAssets.HeaderBytes }).OutputBytes!;
            var context = new BakeFingerprintContext(workspace.Manifest.Target, "coverage-test", "coverage-test");
            var shape = new ProjectOcclusionCoverageSource(EntityId.New(), "Include", true, ProjectOcclusionCoverageRole.Include,
                ProjectTransform.Identity with { Position = new(42, 42, 42) }, BoxSize: new(3, 3, 3));
            var cylinder = new ProjectOcclusionCoverageSource(EntityId.New(), "Cylinder island", true, ProjectOcclusionCoverageRole.Include,
                ProjectTransform.Identity with { Position = new(82, 82, 82) }, Cylinder: new(1, 1), Graph: new(null, [], Disabled: true));
            workspace.SetOcclusionCoverage(new(1, false, [shape, cylinder])); await workspace.SaveAsync();
            var export = Path.Combine(root, "visibility"); Directory.CreateDirectory(export);
            await UyaOcclusionVisibilityService.ExportAsync(projectRoot, "no-iso-required", export, default);
            using (var missing = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(export, "visibility.json"))))
                Check(missing.RootElement.GetProperty("status").GetString() == "needs-build",
                    "Missing authored masks must return an expected status without attempting source extraction.");
            var job = Path.Combine(root, "job"); Directory.CreateDirectory(job);
            var captured = await UyaOcclusionSceneSnapshot.CaptureAsync(workspace, catalog.RootPath, source, null, default);
            await UyaOcclusionCoverageGenerationService.PreparePackedAsync(workspace, captured.LevelWad, job, default);
            await File.WriteAllTextAsync(Path.Combine(job, "project-fingerprint.txt"), workspace.CurrentFingerprint);
            if (exportFixturePath is not null)
            {
                Directory.Move(root, Path.GetFullPath(exportFixturePath));
                return;
            }
            var raw = await File.ReadAllBytesAsync(Path.Combine(job, "scene.json"));
            var scene = JsonSerializer.Deserialize<OcclusionScene>(raw, OcclusionWorkerProtocol.Json)!;
            Check(scene.Cells.Count == 2 && scene.Cells[0].X == 10 && scene.Cells[0].Y == 10 && scene.Cells[0].Z == 10
                && scene.Cells.All(c => c.ReservedVisible) && scene.Cells[1].X == 20, "The worker must sample generated cells instead of source cells, with bit 1023 enabled.");
            var initialGrid = await File.ReadAllBytesAsync(Path.Combine(job, "original-grid.bin"));
            var initialLayout = UyaOcclusionGridReader.Read(initialGrid);
            Check(scene.Preparation is { } preparation && preparation.GridBytes == initialGrid.Length
                && preparation.TreeBytes == initialLayout.MasksOffset && preparation.MaskSlots == 1
                && preparation.UnsharedMaskBytes == 256 && preparation.AlwaysVisibleGroups == 0
                && preparation.CellLimit == OcclusionBakeLimits.MaximumCells && preparation.SampleLimit == OcclusionBakeLimits.MaximumOrigins,
                "Preparation must report SDK-measured grid layout, unshared mask estimates, fallback groups and actual work limits.");
            await WriteResultAsync(job);
            await UyaOcclusionBakeService.CommitAsync(projectRoot, job, default);
            var cache = await UyaOcclusionCoverageGenerationService.ReadAcceptedAsync(workspace, default);
            Check(cache.NativeGrid.Length > (await File.ReadAllBytesAsync(Path.Combine(job, "original-grid.bin"))).Length,
                "Authored visibility must grow its mask allocation instead of merging it into the seed allocation.");
            Check(cache.Coverage!.RecipeHash == workspace.OcclusionCoverageRecipeFingerprint && cache.InputHash == captured.SceneFingerprint,
                "Accepted output must bind the recipe, saved project and exact composed scene.");
            var originalCoverage = workspace.Content.OcclusionCoverage;
            var inactive = shape with { Id = EntityId.New(), Enabled = false };
            workspace.SetOcclusionCoverage(originalCoverage with
            {
                Sources = [.. originalCoverage.Sources.Select(s => s with { Name = "Renamed volume" }), inactive],
            });
            await UyaOcclusionCoverageGenerationService.ReadAcceptedAsync(workspace, default);
            workspace.SetOcclusionCoverage(workspace.Content.OcclusionCoverage with
            {
                Sources = workspace.Content.OcclusionCoverage.Sources.Select(s => s.Id == inactive.Id
                    ? s with { Transform = s.Transform with { Position = new(100, 100, 100) } } : s).ToArray(),
            });
            await workspace.SaveAsync();
            var reopened = await ForgeProjectWorkspace.OpenAsync(projectRoot);
            Check((await UyaOcclusionCoverageGenerationService.ReadAcceptedAsync(reopened, default)).NativeGridHash == cache.NativeGridHash,
                "Renaming volumes and moving inactive volumes preserves accepted visibility after save/reopen.");
            var metadataBake = await UyaBakeService.BakeAsync(projectRoot, catalog, context);
            Check(metadataBake.Succeeded && (await UyaLevelPackService.PackAsync(projectRoot, catalog, source, context)).Succeeded,
                "Harmless coverage edits must permit map baking and packing without rebuilding occlusion.");
            workspace.SetOcclusionCoverage(originalCoverage); await workspace.SaveAsync();
            var editedScene = workspace.CreateProjection();
            editedScene.Rename("Edited scene metadata");
            await ThrowsAsync<InvalidDataException>(() => UyaOcclusionCoverageGenerationService.ReadAcceptedAsync(editedScene, default));
            var savedFingerprint = workspace.CurrentFingerprint;
            var entities = workspace.Content.Entities.Select(e => e.EntityId).ToArray();
            workspace.SetEntityState(entities, true, null, true);
            Check(workspace.CurrentFingerprint != savedFingerprint
                && (await UyaOcclusionCoverageGenerationService.ReadAcceptedAsync(workspace, default)).NativeGridHash == cache.NativeGridHash,
                "Hiding and locking scene objects must preserve accepted visibility before saving.");
            await workspace.SaveAsync();
            workspace = await ForgeProjectWorkspace.OpenAsync(projectRoot);
            Check((await UyaOcclusionCoverageGenerationService.ReadAcceptedAsync(workspace, default)).NativeGridHash == cache.NativeGridHash,
                "Editor visibility must preserve accepted coverage after save and reopen.");
            var disabledScene = workspace.CreateProjection();
            disabledScene.SetEntityState([entities[0]], null, true, null);
            await ThrowsAsync<InvalidDataException>(() => UyaOcclusionCoverageGenerationService.ReadAcceptedAsync(disabledScene, default));
            await ThrowsAsync<InvalidDataException>(() => UyaOcclusionBakeService.ApplyAsync(projectRoot, [..captured.LevelWad, 0], default));
            var applied = await UyaOcclusionBakeService.ApplyAsync(projectRoot, captured.LevelWad, default);
            Check(applied is { Succeeded: true, OutputBytes: not null }
                && OcclusionCoverageBuilder.ReadCoordinates(GameId.UYA, OcclusionComposer.ReadGrid(GameId.UYA, applied.OutputBytes)).First().X == 10,
                "Packing must apply the generated coordinate set through the SDK.");
            await UyaOcclusionVisibilityService.ExportAsync(projectRoot, "no-iso-required", export, default);
            var visibility = JsonSerializer.Deserialize<OcclusionVisibilityData>(await File.ReadAllBytesAsync(Path.Combine(export, "visibility.json")), OcclusionWorkerProtocol.Json)!;
            Check(visibility.Octants.Count == 2 && visibility.Octants[0].X == 10 && visibility.CoverageRecipeFingerprint == workspace.OcclusionCoverageRecipeFingerprint,
                "Visibility inspection must use generated octants with Primary disabled.");
            var bake = await UyaBakeService.BakeAsync(projectRoot, catalog, context);
            Check(bake.Succeeded, "A current authored visibility result must permit normal layer baking.");
            var packed = await UyaLevelPackService.PackAsync(projectRoot, catalog, source, context);
            Check(packed.Succeeded, "Normal packing must accept the same scene identity as isolated preparation.");
            workspace.SetEntityState(entities, false, null, false); await workspace.SaveAsync();
            await UyaOcclusionCoverageGenerationService.ReadAcceptedAsync(workspace, default);
            var before = await File.ReadAllBytesAsync(Path.Combine(projectRoot, "cache/occlusion/generated.json"));
            await ThrowsAsync<OperationCanceledException>(() => UyaOcclusionBakeService.CommitAsync(projectRoot, job, new(true)));
            workspace.SetOcclusionCoverage(new(1, false, [shape with { Cylinder = new(1, 1), BoxSize = null }])); await workspace.SaveAsync();
            await ThrowsAsync<InvalidDataException>(() => UyaOcclusionCoverageGenerationService.ReadAcceptedAsync(workspace, default));
            await UyaOcclusionVisibilityService.ExportAsync(projectRoot, "no-iso-required", export, default);
            using (var stale = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(export, "visibility.json"))))
                Check(stale.RootElement.GetProperty("status").GetString() == "needs-build",
                    "Stale authored masks must return an expected status while map building still rejects them.");
            var cachePath = Path.Combine(projectRoot, "cache/occlusion/generated.json");
            try
            {
                await File.WriteAllTextAsync(cachePath, "malformed");
                await ThrowsAsync<InvalidDataException>(() => UyaOcclusionVisibilityService.ExportAsync(projectRoot, "no-iso-required", export, default));
            }
            finally { await File.WriteAllBytesAsync(cachePath, before); }
            await ThrowsAsync<InvalidDataException>(() => UyaOcclusionBakeService.CommitAsync(projectRoot, job, default));
            var blocked = await UyaBakeValidationService.PreflightAsync(projectRoot, catalog, context);
            Check(!blocked.CanBake && blocked.Diagnostics.Any(d => d.Code == "UYA_OCCLUSION_COVERAGE_NOT_GENERATED"),
                "A recipe edit must invalidate accepted coverage before map building.");
            var retained = await File.ReadAllBytesAsync(Path.Combine(projectRoot, "cache/occlusion/generated.json"));
            Check(before.SequenceEqual(retained),
                "Cancelled and stale jobs must preserve the last accepted cache.");
            // A later generation must replace, rather than union with, the previously accepted coordinates.
            await UyaOcclusionCoverageGenerationService.PreparePackedAsync(workspace, captured.LevelWad, job, default);
            await File.WriteAllTextAsync(Path.Combine(job, "project-fingerprint.txt"), workspace.CurrentFingerprint);
            await WriteResultAsync(job);
            await UyaOcclusionBakeService.CommitAsync(projectRoot, job, default);
            var replaced = await UyaOcclusionCoverageGenerationService.ReadAcceptedAsync(workspace, default);
            Check(replaced.Octants == 1, "Rebuilding a cylinder must remove the previous box/island cells.");
            var graph = shape with { BoxSize = null, Graph = new(new(1, 1, 1), [new(EntityId.New(), "Seed", new(0, 0, 0))]) };
            workspace.SetOcclusionCoverage(new(1, false, [graph])); await workspace.SaveAsync();
            await UyaOcclusionCoverageGenerationService.PreparePackedAsync(workspace, captured.LevelWad, job, default);
            await File.WriteAllTextAsync(Path.Combine(job, "project-fingerprint.txt"), workspace.CurrentFingerprint);
            await WriteResultAsync(job); await UyaOcclusionBakeService.CommitAsync(projectRoot, job, default);
            Check((await UyaOcclusionCoverageGenerationService.ReadAcceptedAsync(workspace, default)).Coverage!.AlgorithmVersion == 3,
                "Graph generation must reuse the same current packed scene and bind the graph algorithm version.");
#if FORGE_OCCLUSION_COVERAGE_GRAPH_SHAPES
            var cylindricalGraph = graph with { Cylinder = new(6, 8), Graph = graph.Graph! with { Size = null } };
            workspace.SetOcclusionCoverage(new(1, false, [cylindricalGraph])); await workspace.SaveAsync();
            await UyaOcclusionCoverageGenerationService.PreparePackedAsync(workspace, captured.LevelWad, job, default);
            var circularScene = JsonSerializer.Deserialize<OcclusionScene>(await File.ReadAllBytesAsync(Path.Combine(job, "scene.json")), OcclusionWorkerProtocol.Json)!;
            var p = cylindricalGraph.Transform.Position; var q = cylindricalGraph.Transform.Rotation;
            var allowed = OcclusionCoverageBuilder.PreviewShapes(GameId.UYA, [],
                [new RatchetPs2.Core.Occlusion.OcclusionCoverageCylinder(new(p.X, p.Y, p.Z), new(q.X, q.Y, q.Z, q.W), 6, 8)]).Cells
                .Select(c => (c.X, c.Y, c.Z)).ToHashSet();
            Check(circularScene.Cells.Count > 0 && circularScene.Cells.All(c => allowed.Contains((c.X, c.Y, c.Z))),
                "Native qualification receives only cells intersecting the owning cylinder.");
            await File.WriteAllTextAsync(Path.Combine(job, "project-fingerprint.txt"), workspace.CurrentFingerprint);
            await WriteResultAsync(job); await UyaOcclusionBakeService.CommitAsync(projectRoot, job, default);
            Check((await UyaOcclusionCoverageGenerationService.ReadAcceptedAsync(workspace, default)).Octants == circularScene.Cells.Count,
                "Cylinder graph cells survive native preparation and accepted visibility publication.");
#endif
            workspace.SetOcclusionCoverage(new(1, false, [])); await workspace.SaveAsync();
            await ThrowsAsync<InvalidDataException>(() => UyaOcclusionCoverageGenerationService.PreparePackedAsync(workspace, captured.LevelWad, job, default));
            workspace.SetOcclusionCoverage(ProjectOcclusionCoverageState.PrimaryOnly); await workspace.SaveAsync();
            Check(await UyaOcclusionBakeService.ApplyAsync(projectRoot, captured.LevelWad, default) is null,
                "Restoring Primary must not apply a previous authored result.");
            Console.WriteLine("PASS authored coverage preparation, visibility commit, inspection, packing and invalidation");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static async Task WriteResultAsync(string job)
    {
        var raw = await File.ReadAllBytesAsync(Path.Combine(job, "scene.json"));
        var scene = JsonSerializer.Deserialize<OcclusionScene>(raw, OcclusionWorkerProtocol.Json)!;
        var samples = new byte[scene.Origins.Count * 256];
        for (var i = 0; i < scene.Cells.Count; i++) samples[scene.Cells[i].Samples[0] * 256] = (byte)(1 << (i % 8));
        var result = new OcclusionBakeResult(2, OcclusionWorkerProtocol.Profile, scene.InputHash, OcclusionWorkerProtocol.Hash(raw),
            scene.Origins.Count, scene.Cells.Count, (scene.Origins.Count + 255) / 256, 1, null, Convert.ToBase64String(samples));
        await File.WriteAllBytesAsync(Path.Combine(job, "result.json"), JsonSerializer.SerializeToUtf8Bytes(result, OcclusionWorkerProtocol.Json));
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}."); }
}
#endif
