using Forge.Host.Games.UYA;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.IO.Compression;
using System.Text.Json;
using Forge.Host.Domain;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.Gameplay;
using RatchetPs2.Core.IO;
using RatchetPs2.Core.LevelAssets;
using RatchetPs2.Core.Skyboxes;
using RatchetPs2.Core.Textures.Palettes;
using RatchetPs2.Core.Textures.Pif;
using RatchetPs2.Core.Wad;
using RatchetPs2.Core.Wad.Models;
using RatchetPs2.Games.UYA.Gameplay;
using RatchetPs2.Games.UYA.Level;
using RatchetPs2.Sdk;

namespace Forge.Host.ProtocolTests.Games.UYA;

internal static class UyaBakeWorkflowTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-uya-bake-workflow-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var catalog = await AssetCatalogStore.OpenAsync(Path.Combine(root, "catalog"));
            await PutAsync(catalog, AssetKind.Moby, 100, 0x11);
            var baseTie = await PutAsync(catalog, AssetKind.Tie, 200, 0x22);
            await PutAsync(catalog, AssetKind.Shrub, 300, 0x33);
            var iso = UyaProjectTests.CreateIso();
            var project = Path.Combine(root, "project");
            await UyaProjectService.CreateValidatedAsync(
                new MemoryStream(iso, writable: false),
                catalog,
                new("synthetic.iso", catalog.RootPath, project, "Bake workflow", new string('a', 32), "1.00", 3, true));
            var context = new BakeFingerprintContext(
                new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"), "translator-1", "baker-1");
            using var isoStream = new MemoryStream(iso, writable: false);
            var sourceLevelWad = UyaLooseLevelWadExtractor.ExtractPrimary(isoStream, 3).Bytes;
            var proxyBytes = CreateCollisionProxy();
            var selectedTie = UyaCanonicalAssetCodec.Decode(CanonicalAsset(AssetKind.Tie, 0x44));
            var retainedAssets = UyaLevelPackService.IncludeSourceStaticAssets(sourceLevelWad,
            [
                new("selected", TextureAssetFamily.Tie, 200, selectedTie.DefinitionBytes,
                    selectedTie.ModelBytes, selectedTie.Textures.Select(value => new StaticAssetTexture(
                        TextureRole.Material, value.PifBytes)).ToArray()),
            ]);
            Equal(3, retainedAssets.Count, "cross-level composition retains the complete base class inventory");
            Equal(0x44, retainedAssets.Single(value => value.Family == TextureAssetFamily.Tie).ModelBytes.Span[0],
                "selected cross-level class overlays its base definition");
            var refreshedAssets = UyaLevelPackService.IncludeSourceStaticAssets(
                sourceLevelWad,
                retainedAssets,
                new HashSet<(TextureAssetFamily, int)> { (TextureAssetFamily.Tie, 200) });
            var refreshedTie = refreshedAssets.Single(value => value.Family == TextureAssetFamily.Tie);
            Equal(false, refreshedTie.ModelBytes.Span[0] == 0x44,
                "source-level catalog classes refresh their model from the clean level");
            Equal(true, refreshedTie.PreserveTextureIndexes,
                "source-level catalog classes preserve retail texture sharing");
            Equal(true, refreshedTie.Textures.Single().PifBytes.Span.SequenceEqual(
                    retainedAssets.Single(value => value.Family == TextureAssetFamily.Tie)
                        .Textures.Single().PifBytes.Span),
                "source-level model refresh preserves its catalog texture");

            var unbaked = await UyaLevelPackService.PackAsync(project, catalog, sourceLevelWad, context);
            Equal(false, unbaked.Succeeded, "unbaked staging cannot pack");
            Equal(null, unbaked.OutputBytes, "failed pack exposes no output");

            var deleteBeforeBakeProject = Path.Combine(root, "delete-before-bake");
            await UyaProjectService.CreateValidatedAsync(
                new MemoryStream(iso, writable: false),
                catalog,
                new("synthetic.iso", catalog.RootPath, deleteBeforeBakeProject, "Delete before bake",
                    new string('a', 32), "1.00", 3, true));
            var deleteBeforeBakeWorkspace = await ForgeProjectWorkspace.OpenAsync(deleteBeforeBakeProject);
            deleteBeforeBakeWorkspace.RemoveEntity(deleteBeforeBakeWorkspace.Content.Entities
                .Single(value => value.Layer == "ties").EntityId);
            await deleteBeforeBakeWorkspace.SaveAsync();
            Equal(true, (await UyaBakeService.BakeAsync(deleteBeforeBakeProject, catalog, context)).Succeeded,
                "tie deletion before initial build bakes");
            var deleteBeforeBakePack = await UyaLevelPackService.PackAsync(
                deleteBeforeBakeProject, catalog, sourceLevelWad, context);
            Equal(true, deleteBeforeBakePack.Succeeded, "tie deletion before initial build packs: "
                + string.Join(" | ", deleteBeforeBakePack.Diagnostics.Select(value => value.Cause)));

            var copyBeforeBakeProject = Path.Combine(root, "copy-before-bake");
            await UyaProjectService.CreateValidatedAsync(
                new MemoryStream(iso, writable: false),
                catalog,
                new("synthetic.iso", catalog.RootPath, copyBeforeBakeProject, "Copy before bake",
                    new string('a', 32), "1.00", 3, true));
            var copyBeforeBakeWorkspace = await ForgeProjectWorkspace.OpenAsync(copyBeforeBakeProject);
            var copiedTie = copyBeforeBakeWorkspace.AddCopies([
                copyBeforeBakeWorkspace.Content.Entities.Single(value => value.Layer == "ties"),
            ]).Single();
            Equal(null, copiedTie.Provenance, "copied tie clears provenance");
            Equal(0, copiedTie.Source!.SourceIndex, "copied tie retains source lineage");
            await copyBeforeBakeWorkspace.SaveAsync();
            Equal(true, (await UyaBakeService.BakeAsync(copyBeforeBakeProject, catalog, context)).Succeeded,
                "tie copy before initial build bakes");
            var copyBeforeBakePack = await UyaLevelPackService.PackAsync(
                copyBeforeBakeProject, catalog, sourceLevelWad, context);
            Equal(true, copyBeforeBakePack.Succeeded, "tie copy before initial build packs: "
                + string.Join(" | ", copyBeforeBakePack.Diagnostics.Select(value => value.Cause)));
            var copiedTieFiles = UyaLevelWadUnpacker.Unpack(copyBeforeBakePack.OutputBytes!).Files;
            var copiedTies = UyaTieInstancesReader.Read(copiedTieFiles
                .Single(value => value.Path == "gameplay/core/tie_instances.bin").Bytes);
            Equal(2, copiedTies.Count,
                "packed tie copy adds an instance");
            Equal(true, copiedTies.Instances.Select(value => BitConverter.ToInt32(
                    value.RawBytes, UyaTieInstancesReader.OcclusionIdOffset)).SequenceEqual(new[] { 77, 77 }),
                "packed tie copy reuses its source occlusion ID");
            var copiedTieAmbient = UyaGameplayLightingReader.ReadTieAmbientRgbas(copiedTieFiles
                .Single(value => value.Path == "gameplay/core/tie_ambient_rgbas.bin").Bytes, 2);
            Equal(true, copiedTieAmbient[0].SequenceEqual(copiedTieAmbient[1]),
                "packed tie copy duplicates ambient lighting");
            Equal(true, UyaTieGroupsReader.Read(copiedTieFiles
                .Single(value => value.Path == "gameplay/core/tie_groups.bin").Bytes).Groups.Single()
                .SequenceEqual(new[] { 0, 1 }), "packed tie copy duplicates group membership");
            var copiedTieMappings = UyaOcclusionMappingsReader.Read(copiedTieFiles
                .Single(value => value.Path == "gameplay/core/occlusion.bin").Bytes).Ties;
            Equal(true, copiedTieMappings.Select(value => (value.BitIndex, value.OcclusionId))
                    .SequenceEqual(new[] { (5, 77), (5, 77) }),
                "packed tie copy reuses its source occlusion mapping");

            var shrubProxyProject = Path.Combine(root, "shrub-proxy");
            await UyaProjectService.CreateValidatedAsync(
                new MemoryStream(iso, writable: false),
                catalog,
                new("synthetic.iso", catalog.RootPath, shrubProxyProject, "Shrub proxy",
                    new string('a', 32), "1.00", 3, true));
            var shrubProxyWorkspace = await ForgeProjectWorkspace.OpenAsync(shrubProxyProject);
            var shrubProxyInstances = shrubProxyWorkspace.Content.Entities
                .Where(value => value.Asset?.Kind == AssetKind.Shrub && value.State?.Disabled != true)
                .ToArray();
            var shrubProxyAssetId = shrubProxyInstances.First().Asset!.Id;
            shrubProxyInstances = shrubProxyInstances
                .Where(value => value.Asset!.Id == shrubProxyAssetId)
                .ToArray();
            var shrubBaselineBake = await UyaBakeService.BakeAsync(shrubProxyProject, catalog, context);
            Equal(true, shrubBaselineBake.Succeeded, "shrub proxy baseline bakes");
            var shrubBaselinePack = await UyaLevelPackService.PackAsync(
                shrubProxyProject, catalog, sourceLevelWad, context);
            Equal(true, shrubBaselinePack.Succeeded, "shrub proxy baseline packs");
            var shrubBaselineCollision = UyaBaseLayerService.Extract(
                    UyaLevelWadUnpacker.Unpack(shrubBaselinePack.OutputBytes!))
                .Single(value => value.Layer == BakeLayerId.Collision && value.Name == "collision.bin").Bytes;
            await shrubProxyWorkspace.ApplyInstancedCollisionProxyAsync(
                shrubProxyAssetId,
                proxyBytes,
                UyaBaseLayerSchema.CanonicalFormatVersion,
                new(ProjectInstancedCollisionRecipeKind.Surface, 1, 1, 0, 0x3d));
            shrubProxyWorkspace.SetInstancedCollisionEnabled(shrubProxyInstances[0].EntityId, true);
            await shrubProxyWorkspace.SaveAsync();
            var shrubProxyBake = await UyaBakeService.BakeAsync(shrubProxyProject, catalog, context);
            Equal(true, shrubProxyBake.WrittenLayers.Select(value => value.Layer)
                .SequenceEqual([BakeLayerId.Collision]),
                "adding a shrub collision proxy rebuilds only collision");
            var shrubProxyPack = await UyaLevelPackService.PackAsync(
                shrubProxyProject, catalog, sourceLevelWad, context);
            Equal(true, shrubProxyPack.Succeeded, "instanced shrub collision staging packs");
            var shrubProxyCollision = UyaBaseLayerService.Extract(
                    UyaLevelWadUnpacker.Unpack(shrubProxyPack.OutputBytes!))
                .Single(value => value.Layer == BakeLayerId.Collision && value.Name == "collision.bin").Bytes;
            Equal(
                CollisionConverter.Inspect(shrubBaselineCollision, GameId.UYA).Pieces
                    .Sum(value => value.FaceCount) + 1,
                CollisionConverter.Inspect(shrubProxyCollision, GameId.UYA).Pieces
                    .Sum(value => value.FaceCount),
                "bake expands the shared proxy only for the selected shrub instance");
            Equal(true, shrubProxyWorkspace.Content.Entities
                    .Where(value => value.Asset?.Id == shrubProxyAssetId)
                    .Count(value => value.InstancedCollisionEnabled == true) == 1,
                "matching shrub instances retain independent collision choices");

            var insertBeforeBakeProject = Path.Combine(root, "insert-before-bake");
            await UyaProjectService.CreateValidatedAsync(
                new MemoryStream(iso, writable: false),
                catalog,
                new("synthetic.iso", catalog.RootPath, insertBeforeBakeProject, "Insert before bake",
                    new string('a', 32), "1.00", 3, true));
            var insertBeforeBakeWorkspace = await ForgeProjectWorkspace.OpenAsync(insertBeforeBakeProject);
            insertBeforeBakeWorkspace.RemoveEntity(insertBeforeBakeWorkspace.Content.Entities
                .Single(value => value.Layer == "shrubs").EntityId);
            var insertedRecord = UyaAssetPlacementService.CreateTieRecord(insertBeforeBakeWorkspace, 200);
            Equal(4_000, BinaryPrimitives.ReadInt32LittleEndian(insertedRecord.AsSpan(4)),
                "placed tie uses the retail integer draw distance encoding");
            BinaryPrimitives.WriteSingleLittleEndian(insertedRecord.AsSpan(4), 4_000f);
            var insertedTie = new ProjectEntity(
                EntityId.New(),
                "tie:0x00C8",
                "ties",
                ProjectTransform.Identity,
                new(baseTie.Id, AssetKind.Tie),
                Source: new(200, insertedRecord),
                TieLighting: new(0, UyaAssetPlacementService.CreateNeutralTieAmbient(4)));
            insertBeforeBakeWorkspace.AddEntity(insertedTie);
            await insertBeforeBakeWorkspace.SaveAsync();
            var insertBeforeBake = await UyaBakeService.BakeAsync(insertBeforeBakeProject, catalog, context);
            Equal(true, insertBeforeBake.Succeeded,
                "tie insertion before initial build bakes");
            Equal(2, insertBeforeBake.PaletteReport!.InputTextureCount,
                "bake palette report contains only selected static classes");
            var insertedStaging = await BakeStagingStore.OpenAsync(insertBeforeBakeProject);
            var insertedStaleReport = insertBeforeBake.PaletteReport with
            {
                InputPaletteCount = 1,
                InputPaletteBytes = 1_024,
                EstimatedPaletteVramSavingsBytes = 1_024 - insertBeforeBake.PaletteReport.OutputPaletteBytes,
            };
            await insertedStaging.RestoreManifestAsync(
                insertBeforeBake.Manifest with { PaletteReport = insertedStaleReport });
            var insertBeforeBakePack = await UyaLevelPackService.PackAsync(
                insertBeforeBakeProject, catalog, sourceLevelWad, context);
            Equal(true, insertBeforeBakePack.Succeeded, "tie insertion before initial build packs: "
                + string.Join(" | ", insertBeforeBakePack.Diagnostics.Select(value => value.Cause)));
            Equal(true, PaletteBakeReportService.Equivalent(
                    insertBeforeBake.PaletteReport,
                    (await BakeStagingStore.OpenAsync(insertBeforeBakeProject)).Manifest.PaletteReport!),
                "visibility-bit composition refreshes its staged palette report");
            var insertedTieFiles = UyaLevelWadUnpacker.Unpack(insertBeforeBakePack.OutputBytes!).Files;
            var packedInsertedTie = UyaTieInstancesReader.Read(insertedTieFiles
                .Single(value => value.Path == "gameplay/core/tie_instances.bin").Bytes).Instances
                .Single(value => BinaryPrimitives.ReadInt32LittleEndian(
                    value.RawBytes.AsSpan(UyaTieInstancesReader.OcclusionIdOffset)) == 1);
            Equal(4_000, BinaryPrimitives.ReadInt32LittleEndian(packedInsertedTie.RawBytes.AsSpan(4)),
                "bake repairs legacy placed tie draw distance encoding");
            var insertedMappings = UyaOcclusionMappingsReader.Read(insertedTieFiles
                .Single(value => value.Path == "gameplay/core/occlusion.bin").Bytes).Ties;
            Equal(true, insertedMappings.Select(value => (value.BitIndex, value.OcclusionId))
                    .SequenceEqual(new[] { (5, 77), (0, 1) }),
                "packed tie insertion reserves an always-visible occlusion bit");
            Equal(true, UyaTieGroupsReader.Read(insertedTieFiles
                    .Single(value => value.Path == "gameplay/core/tie_groups.bin").Bytes).Groups.Single()
                    .SequenceEqual(new[] { 0 }),
                "packed tie insertion inherits no source group membership");

            var first = await UyaBakeService.BakeAsync(project, catalog, context);
            Equal(true, first.Succeeded, "initial bake succeeds");
            Equal(10, first.WrittenLayers.Count, "initial bake writes every layer");
            Equal(true, first.Validation.Plan.Layers.All(value => value.State == BakeLayerState.Clean),
                "initial bake validates clean");
            var paletteReport = first.PaletteReport!;
            Equal(paletteReport, first.Manifest.PaletteReport, "bake result exposes its staged palette report");
            Equal(3, paletteReport.InputTextureCount, "palette report input texture count");
            Equal(3, paletteReport.InputPaletteCount, "palette report distinct input palette count");
            Equal(3, paletteReport.OutputPaletteCount, "palette report optimized palette count");
            Equal(3_072L, paletteReport.InputPaletteBytes, "palette report input VRAM estimate");
            Equal(3_072L, paletteReport.OutputPaletteBytes, "palette report output VRAM estimate");
            Equal(0L, paletteReport.EstimatedPaletteVramSavingsBytes, "palette report VRAM savings estimate");
            Equal(true, paletteReport.IsLossless, "vanilla palette report is lossless");
            Equal(0d, paletteReport.ImportedQuantizationError, "vanilla palette report quantization error");
            Equal(PaletteOptimizer.ExactMethod, paletteReport.Optimization.Method,
                "palette report labels its optimization method");
            Equal(true, paletteReport.Optimization.IsProvenOptimal, "small palette report is proven optimal");
            Equal(3, paletteReport.Optimization.Assignments.Count, "palette report traces every texture assignment");
            Equal(6, paletteReport.Optimization.Assignments.Sum(value => value.IndexRemaps.Count),
                "palette report traces every referenced old-to-new index mapping");
            Equal("bdda42b46385d629f6e8c2b8deddaa437c6dad19c58296b09d3c808595a8820b", Convert.ToHexString(SHA256.HashData(
                ForgeProjectPersistence.Serialize(paletteReport))).ToLowerInvariant(),
                "palette report schema snapshot");
            var textureInventory = await UyaTextureInventoryService.BuildAsync(project, catalog);
            Equal(3, textureInventory.Textures.Count, "staged vanilla definitions inventory their textures");
            Equal(12L, textureInventory.TexelCount, "texture inventory counts every selected vanilla texel");
            var optimizedPalettes = PaletteOptimizer.Optimize(textureInventory);
            Equal(0, optimizedPalettes.Violations.Count, "staged vanilla textures optimize without violations");
            Equal(3, optimizedPalettes.Palettes.Count,
                "source textures preserve their retail palette indexes");
            Equal(3, optimizedPalettes.Assignments.Count, "every staged vanilla texture receives a palette assignment");
            var staleReport = paletteReport with
            {
                InputPaletteCount = 2,
                InputPaletteBytes = 2_048,
                EstimatedPaletteVramSavingsBytes = -1_024,
            };
            var staging = await BakeStagingStore.OpenAsync(project);
            await staging.RestoreManifestAsync(first.Manifest with { PaletteReport = staleReport });
            var stalePack = await UyaLevelPackService.PackAsync(project, catalog, sourceLevelWad, context);
            Equal(false, stalePack.Succeeded, "stale palette report cannot accompany packed output");
            Equal(true, stalePack.Diagnostics.Any(value => value.Cause.Contains(
                "palette report does not match", StringComparison.Ordinal)), "stale palette report is actionable");
            await staging.RestoreManifestAsync(first.Manifest);
            var packedSource = await UyaLevelPackService.PackAsync(project, catalog, sourceLevelWad, context);
            Equal(true, packedSource.Succeeded, "validated staging packs: "
                + string.Join(" | ", packedSource.Diagnostics.Select(value => value.Cause)));
            Equal("8e344e0d181cf1fa7d0249e582dbbf457a2fcc294fae6932c9749b60adaa2381",
                packedSource.OutputSha256, "unchanged staged project golden WAD");
            _ = UyaLevelWadInventoryReader.Read(packedSource.OutputBytes!);
            var packedFiles = UyaLevelWadUnpacker.Unpack(packedSource.OutputBytes!).Files;
            Equal(true, ReadClassIds(packedFiles, "moby").SequenceEqual(UyaMobyInstancesReader.Read(packedFiles
                    .Single(value => value.Path == "gameplay/core/moby_instances.bin").Bytes).Instances
                    .Select(value => value.ClassId).Distinct().Order())
                && ReadClassIds(packedFiles, "tie").SequenceEqual(UyaTieInstancesReader.Read(packedFiles
                    .Single(value => value.Path == "gameplay/core/tie_instances.bin").Bytes).Instances
                    .Select(value => value.ClassId).Distinct().Order())
                && ReadClassIds(packedFiles, "shrub").SequenceEqual(UyaShrubInstancesReader.Read(packedFiles
                    .Single(value => value.Path == "gameplay/core/shrub_instances.bin").Bytes).Instances
                    .Select(value => value.ClassId).Distinct().Order()),
                "packed static class lists match their rebuilt instance tables");
            var installed = ReadAssets(packedSource.OutputBytes!);
            Equal(0, installed.AssetWad[installed.Mobys.Single().ModelOffset], "clean source moby model is installed");
            Equal(0, installed.AssetWad[installed.Ties.Single().ModelOffset], "clean source tie model is installed");
            Equal(0, installed.AssetWad[installed.Shrubs.Single().ModelOffset], "clean source shrub model is installed");
            Equal(true, installed.Header is
                { MobyTextureCount: 0, TieTextureCount: 0, ShrubTextureCount: 0 },
                "unchanged source asset payload is retained without recompilation");

            await VerifyCollisionBakeWorkflowAsync(root, catalog, iso, sourceLevelWad, context);

            var compositionSourceSky = await catalog.PutAsync(
                AssetKind.Sky,
                UyaBaseLayerSchema.CanonicalFormatVersion,
                global::EditorRuntimeTests.BuildSkybox(),
                new(
                    "test-sky",
                    new("UYA", "NTSC-U", "1.00", "level41", "level_wad/assets/asset_wad.bin", 0,
                        new string('a', 32)),
                    ["sky:level41"],
                    ["vanilla", "game:UYA", "level:41"]));
            var workspace = await ForgeProjectWorkspace.OpenAsync(project);
            var firstSkyId = EntityId.New();
            var secondSkyId = EntityId.New();
            workspace.AddEntity(SkyShell(firstSkyId, compositionSourceSky.Id, 0, 11, 2));
            workspace.AddEntity(SkyShell(secondSkyId, compositionSourceSky.Id, 1, -7, -3));
            await workspace.SaveAsync();
            var addedSkyBake = await UyaBakeService.BakeAsync(project, catalog, context);
            Equal(true, addedSkyBake.WrittenLayers.Select(value => value.Layer).SequenceEqual([BakeLayerId.Sky]),
                "sky shell addition rebuilds only sky");
            var addedSkyPack = await UyaLevelPackService.PackAsync(project, catalog, sourceLevelWad, context);
            Equal(true, addedSkyPack.Succeeded, "composed sky packs: "
                + string.Join(" | ", addedSkyPack.Diagnostics.Select(value => value.Cause)));
            var addedSky = ReadSkybox(addedSkyPack.OutputBytes!);
            Equal(true, addedSky.Shells.Select(value => (value.RotationX, value.RotationDeltaZ))
                .SequenceEqual(new[] { ((short)11, (short)2), ((short)-7, (short)-3) }),
                "packed sky preserves shell order and effective rotations");

            workspace = await ForgeProjectWorkspace.OpenAsync(project);
            await UyaSkyShellEditorService.ExecuteAsync(
                workspace,
                catalog.RootPath,
                new(
                    Guid.NewGuid().ToString("D"),
                    EditorCommandKind.UpdateSkyShell,
                    [firstSkyId],
                    SkyShellUpdate: new(
                        InitialRotationRadians: new(25 * (MathF.PI / 32768f), 0, 0),
                        AngularVelocityRadiansPerSecond: new(0, 0, 5 * (MathF.PI / 32768f) * 60))),
                CancellationToken.None);
            await workspace.SaveAsync();
            workspace = await ForgeProjectWorkspace.OpenAsync(project);
            Equal(5f, MathF.Round(workspace.Content.Entities.Single(value => value.EntityId == firstSkyId)
                    .SkyShell!.AngularVelocityRadiansPerSecond.Z / ((MathF.PI / 32768f) * 60)),
                "edited sky rotational velocity survives save and reopen");
            var skyValidation = await UyaBakeValidationService.PreflightAsync(project, catalog, context);
            var skyPlan = skyValidation.Plan.Layers.Single(value => value.Layer == BakeLayerId.Sky);
            var stagingBeforeSkyFailure = await BakeStagingStore.OpenAsync(project);
            var manifestBeforeSkyFailure = ManifestBytes(stagingBeforeSkyFailure.Manifest);
            await ThrowsAsync<InvalidDataException>(() => UyaBaseLayerStore.StageAsync(
                project,
                catalog,
                stagingBeforeSkyFailure,
                skyPlan,
                fault: output => File.WriteAllBytes(Path.Combine(output, "sky.bin"), [0]),
                cancellationToken: CancellationToken.None));
            var manifestAfterSkyFailure = (await BakeStagingStore.OpenAsync(project)).Manifest;
            Equal(true, manifestBeforeSkyFailure.SequenceEqual(ManifestBytes(manifestAfterSkyFailure)),
                "invalid composed sky preserves the last good stage");
            var editedSkyBake = await UyaBakeService.BakeAsync(project, catalog, context);
            Equal(true, editedSkyBake.WrittenLayers.Select(value => value.Layer).SequenceEqual([BakeLayerId.Sky]),
                "rotation property edit rebuilds only sky");
            var editedSkyPack = await UyaLevelPackService.PackAsync(project, catalog, sourceLevelWad, context);
            Equal(true, ReadSkybox(editedSkyPack.OutputBytes!).Shells
                .Select(value => (value.RotationX, value.RotationDeltaZ))
                .SequenceEqual(new[] { ((short)25, (short)5), ((short)-7, (short)-3) }),
                "packed sky preserves edited initial rotation and rotational velocity");

            workspace.ReorderSkyShell(secondSkyId, 0);
            await workspace.SaveAsync();
            var reorderedSkyBake = await UyaBakeService.BakeAsync(project, catalog, context);
            Equal(true, reorderedSkyBake.WrittenLayers.Select(value => value.Layer).SequenceEqual([BakeLayerId.Sky]),
                "sky shell reorder rebuilds only sky");
            var reorderedSkyPack = await UyaLevelPackService.PackAsync(project, catalog, sourceLevelWad, context);
            Equal(true, ReadSkybox(reorderedSkyPack.OutputBytes!).Shells
                .Select(value => value.RotationX).SequenceEqual(new short[] { -7, 25 }),
                "packed sky preserves reordered shells");

            workspace = await ForgeProjectWorkspace.OpenAsync(project);
            workspace.RemoveEntities([firstSkyId, secondSkyId]);
            await workspace.SaveAsync();
            var restoredSkyBake = await UyaBakeService.BakeAsync(project, catalog, context);
            Equal(true, restoredSkyBake.WrittenLayers.Select(value => value.Layer).SequenceEqual([BakeLayerId.Sky]),
                "sky shell removal rebuilds only sky");
            var restoredSkyPack = await UyaLevelPackService.PackAsync(project, catalog, sourceLevelWad, context);
            Equal(packedSource.OutputSha256, restoredSkyPack.OutputSha256,
                "restoring the imported sky composition restores byte-identical output");

            workspace = await ForgeProjectWorkspace.OpenAsync(project);
            var directionalLight = workspace.Content.Entities.Single(
                value => value.Lighting?.DirectionalLight is not null);
            workspace.UpdateTransform(directionalLight.EntityId, directionalLight.Transform with
            {
                Rotation = new(0, 0, 1, 0),
            });
            await workspace.SaveAsync();
            var directionalLightBake = await UyaBakeService.BakeAsync(project, catalog, context);
            Equal(true, directionalLightBake.WrittenLayers.Select(value => value.Layer)
                .SequenceEqual([BakeLayerId.Lighting]), "directional-light edit rebuilds only lighting");
            var packedDirectionalLight = await UyaLevelPackService.PackAsync(project, catalog, sourceLevelWad, context);
            Equal(true, packedDirectionalLight.Succeeded, "edited directional-light staging packs: "
                + string.Join(" | ", packedDirectionalLight.Diagnostics.Select(value => value.Cause)));
            var packedDirectionalLights = UyaGameplayLightingReader.ReadDirectionalLights(UyaLevelWadUnpacker
                .Unpack(packedDirectionalLight.OutputBytes!).Files
                .Single(value => value.Path == "gameplay/core/directional_lights.bin").Bytes);
            Equal(-0.75f, packedDirectionalLights.Single().InverseDirection.X,
                "packed WAD preserves edited directional-light rotation");

            workspace = await ForgeProjectWorkspace.OpenAsync(project);
            var pointLight = workspace.Content.Entities.Single(value => value.Lighting?.PointLight is not null);
            workspace.UpdateTransform(pointLight.EntityId, pointLight.Transform with
            {
                Position = new(48, 24, 12),
                Scale = new(10, 10, 10),
            });
            await workspace.SaveAsync();
            var pointLightBake = await UyaBakeService.BakeAsync(project, catalog, context);
            Equal(true, pointLightBake.WrittenLayers.Select(value => value.Layer).SequenceEqual([BakeLayerId.Lighting]),
                "point-light edit rebuilds only lighting");
            var packedPointLight = await UyaLevelPackService.PackAsync(project, catalog, sourceLevelWad, context);
            Equal(true, packedPointLight.Succeeded, "edited point-light staging packs: "
                + string.Join(" | ", packedPointLight.Diagnostics.Select(value => value.Cause)));
            var packedPointLights = UyaGameplayLightingReader.ReadPointLights(UyaLevelWadUnpacker
                .Unpack(packedPointLight.OutputBytes!).Files
                .Single(value => value.Path == "gameplay/core/point_lights.bin").Bytes);
            Equal(new GameplayVector3(48, 24, 12), packedPointLights.Lights.Single().Position,
                "packed WAD preserves edited point-light position");
            Equal(10f, packedPointLights.Lights.Single().Radius,
                "packed WAD preserves edited point-light radius");
            Equal(true, packedPointLights.MasksMatchDerived,
                "packed WAD rebuilds point-light masks");

            workspace = await ForgeProjectWorkspace.OpenAsync(project);
            var environmentSample = workspace.Content.Entities.Single(
                value => value.Lighting?.EnvironmentSamplePoint is not null);
            workspace.UpdateTransform(environmentSample.EntityId, environmentSample.Transform with
            {
                Position = new(-1.25f, 2.5f, 3.75f),
            });
            await workspace.SaveAsync();
            var environmentSampleBake = await UyaBakeService.BakeAsync(project, catalog, context);
            Equal(true, environmentSampleBake.WrittenLayers.Select(value => value.Layer)
                .SequenceEqual([BakeLayerId.Gameplay]), "environment-sample edit rebuilds only gameplay");
            var packedEnvironmentSample = await UyaLevelPackService.PackAsync(
                project, catalog, sourceLevelWad, context);
            Equal(true, packedEnvironmentSample.Succeeded, "edited environment-sample staging packs: "
                + string.Join(" | ", packedEnvironmentSample.Diagnostics.Select(value => value.Cause)));
            var packedEnvironmentSamples = UyaGameplayLightingReader.ReadEnvironmentSamplePoints(
                UyaLevelWadUnpacker.Unpack(packedEnvironmentSample.OutputBytes!).Files
                    .Single(value => value.Path == "gameplay/core/env_sample_points.bin").Bytes);
            Equal(new GameplayVector3(-1.25f, 2.5f, 3.75f), packedEnvironmentSamples.Single().Position,
                "packed WAD preserves edited environment-sample position");

            workspace = await ForgeProjectWorkspace.OpenAsync(project);
            var environmentTransition = workspace.Content.Entities.Single(
                value => value.Lighting?.EnvironmentTransition is not null);
            workspace.UpdateTransform(environmentTransition.EntityId, environmentTransition.Transform with
            {
                Position = new(4, 5, 6),
                Scale = new(2, 3, 4),
            });
            await workspace.SaveAsync();
            var environmentTransitionBake = await UyaBakeService.BakeAsync(project, catalog, context);
            Equal(true, environmentTransitionBake.WrittenLayers.Select(value => value.Layer)
                .SequenceEqual([BakeLayerId.Gameplay]), "environment-transition edit rebuilds only gameplay");
            var packedEnvironmentTransition = await UyaLevelPackService.PackAsync(
                project, catalog, sourceLevelWad, context);
            Equal(true, packedEnvironmentTransition.Succeeded, "edited environment-transition staging packs: "
                + string.Join(" | ", packedEnvironmentTransition.Diagnostics.Select(value => value.Cause)));
            var packedEnvironmentTransitions = UyaGameplayLightingReader.ReadEnvironmentTransitions(
                UyaLevelWadUnpacker.Unpack(packedEnvironmentTransition.OutputBytes!).Files
                    .Single(value => value.Path == "gameplay/core/env_transitions.bin").Bytes);
            Equal(new GameplayVector4(4, 5, 6, MathF.Sqrt(29)),
                packedEnvironmentTransitions.Single().BoundingSphere,
                "packed WAD rebuilds environment-transition bounds");
            Equal(new ProjectVector3(4, 5, 6),
                UyaBaseLevelService.InvertAndDecompose(
                    packedEnvironmentTransitions.Single().InverseMatrix)!.Position,
                "packed WAD preserves edited environment-transition transform");

            workspace = await ForgeProjectWorkspace.OpenAsync(project);
            var spline = workspace.Content.Entities.Single(value => value.Geometry?.Spline is not null);
            var editedSplinePoints = spline.Geometry!.Spline!.Points
                .Select((value, index) => index == 0 ? value with { W = value.W + 1 } : value).ToArray();
            workspace.UpdateSplinePoints(spline.EntityId, editedSplinePoints);
            await workspace.SaveAsync();
            var splineBake = await UyaBakeService.BakeAsync(project, catalog, context);
            Equal(true, splineBake.WrittenLayers.Select(value => value.Layer).SequenceEqual([BakeLayerId.Gameplay]),
                "spline edit rebuilds only gameplay");
            var packedSpline = await UyaLevelPackService.PackAsync(project, catalog, sourceLevelWad, context);
            Equal(true, packedSpline.Succeeded, "edited spline staging packs: "
                + string.Join(" | ", packedSpline.Diagnostics.Select(value => value.Cause)));
            var packedSplineBytes = UyaLevelWadUnpacker.Unpack(packedSpline.OutputBytes!).Files
                .Single(value => value.Path == "gameplay/core/splines.bin").Bytes;
            Equal(editedSplinePoints[0].W, GameplayGeometryReader.ReadSplines(packedSplineBytes).Single().Points[0].W,
                "packed WAD preserves edited spline data");

            workspace = await ForgeProjectWorkspace.OpenAsync(project);
            var grindPath = workspace.Content.Entities.Single(value => value.Geometry?.GrindPath is not null);
            var editedGrindPoints = grindPath.Geometry!.GrindPath!.Points
                .Append(grindPath.Geometry.GrindPath.Points[^1] with { X = 9, W = 12 }).ToArray();
            workspace.UpdateSplinePoints(grindPath.EntityId, editedGrindPoints);
            workspace.UpdateTransform(grindPath.EntityId, grindPath.Transform with { Position = new(10, 20, 30) });
            await workspace.SaveAsync();
            var grindPathBake = await UyaBakeService.BakeAsync(project, catalog, context);
            Equal(true, grindPathBake.WrittenLayers.Select(value => value.Layer).SequenceEqual([BakeLayerId.Gameplay]),
                "grind-path edit rebuilds only gameplay");
            var packedGrindPath = await UyaLevelPackService.PackAsync(project, catalog, sourceLevelWad, context);
            Equal(true, packedGrindPath.Succeeded, "edited grind-path staging packs: "
                + string.Join(" | ", packedGrindPath.Diagnostics.Select(value => value.Cause)));
            var packedGrindPaths = GameplayGeometryReader.ReadGrindPaths(UyaLevelWadUnpacker
                .Unpack(packedGrindPath.OutputBytes!).Files
                .Single(value => value.Path == "gameplay/core/grind_splines.bin").Bytes);
            Equal(new GameplayVector4(19, 20, 30, 12), packedGrindPaths.Single().Points[^1],
                "packed WAD preserves edited and transformed grind-path points");
            Equal(11, packedGrindPaths.Single().Unknown4, "packed WAD preserves grind-path metadata");

            var crossLevelTie = await PutAsync(catalog, AssetKind.Tie, 200, 0x44, "level45");
            var contentPath = (await ForgeProjectWorkspace.OpenAsync(project)).ContentFilePath;
            await ReplaceCompressedTextAsync(
                contentPath, baseTie.Id.ToString(), crossLevelTie.Id.ToString());
            var crossLevelBake = await UyaBakeService.BakeAsync(project, catalog, context);
            Equal(true, crossLevelBake.WrittenLayers.Select(value => value.Layer)
                .SequenceEqual([BakeLayerId.Ties, BakeLayerId.Lighting]),
                "cross-level tie asset invalidates only ties and lighting");
            var crossLevelPack = await UyaLevelPackService.PackAsync(project, catalog, sourceLevelWad, context);
            Equal(true, crossLevelPack.Succeeded, "cross-level tie asset packs");
            var crossLevelAssets = ReadAssets(crossLevelPack.OutputBytes!);
            Equal(0x44, crossLevelAssets.AssetWad[crossLevelAssets.Ties.Single().ModelOffset],
                "cross-level selected tie model replaces the base-level definition");

            using (var packCancellation = new CancellationTokenSource())
            {
                await ThrowsAsync<OperationCanceledException>(() => UyaLevelPackService.PackAsync(
                    project,
                    catalog,
                    sourceLevelWad,
                    context,
                    progress: new InlineProgress<LevelArchiveProgress>(value =>
                    {
                        if (value.Phase == LevelArchivePhase.Inventory) packCancellation.Cancel();
                    }),
                    cancellationToken: packCancellation.Token));
            }

            var firstManifest = ManifestBytes(crossLevelBake.Manifest);
            var noOp = await UyaBakeService.BakeAsync(project, catalog, context);
            Equal(true, noOp.IsCurrent, "no-op bake reports current staging");
            Equal(0, noOp.WrittenLayers.Count, "no-op bake writes no layers");
            Equal(true, firstManifest.SequenceEqual(ManifestBytes(noOp.Manifest)),
                "no-op bake preserves manifest bytes");

            workspace = await ForgeProjectWorkspace.OpenAsync(project);
            var tie = workspace.Content.Entities.Single(value => value.Layer == "ties");
            workspace.UpdateTransform(tie.EntityId, tie.Transform with
            {
                Position = tie.Transform.Position with { X = tie.Transform.Position.X + 1 },
            });
            await workspace.SaveAsync();
            var incremental = await UyaBakeService.BakeAsync(project, catalog, context);
            Equal(true, incremental.WrittenLayers.Select(value => value.Layer)
                .SequenceEqual([BakeLayerId.Ties, BakeLayerId.Lighting]),
                "tie edit rebuilds only ties and lighting");
            foreach (var prior in noOp.Manifest.Layers.Where(value => value.Layer is not BakeLayerId.Ties and not BakeLayerId.Lighting))
                Equal(prior, incremental.Manifest.Layers.Single(value => value.Layer == prior.Layer),
                    $"tie edit preserves {prior.Layer} snapshot");
            var packedEdit = await UyaLevelPackService.PackAsync(project, catalog, sourceLevelWad, context);
            Equal(true, packedEdit.Succeeded, "incremental staging packs");
            var packedTieBytes = UyaLevelWadUnpacker.Unpack(packedEdit.OutputBytes!).Files
                .Single(value => value.Path == "gameplay/core/tie_instances.bin").Bytes;
            Equal(41f, UyaTieInstancesReader.Read(packedTieBytes).Instances.Single().Transform.Position.X,
                "packed WAD preserves staged tie translation");

            workspace = await ForgeProjectWorkspace.OpenAsync(project);
            tie = workspace.Content.Entities.Single(value => value.Layer == "ties");
            workspace.UpdateTransform(tie.EntityId, tie.Transform with
            {
                Position = tie.Transform.Position with { X = tie.Transform.Position.X + 1 },
            });
            await workspace.SaveAsync();
            var deferred = new HashSet<BakeLayerId> { BakeLayerId.Ties, BakeLayerId.Lighting };
            var selective = await UyaBakeService.BakeAsync(
                project, catalog, context, includedLayers: new HashSet<BakeLayerId>());
            Equal(false, selective.IsCurrent, "selective bake reports deferred layers");
            Equal(0, selective.WrittenLayers.Count, "selective bake leaves unchecked layers staged");
            var packedDeferred = await UyaLevelPackService.PackAsync(
                project, catalog, sourceLevelWad, context, deferredLayers: deferred);
            Equal(true, packedDeferred.Succeeded, "deferred staging packs");
            var deferredTieBytes = UyaLevelWadUnpacker.Unpack(packedDeferred.OutputBytes!).Files
                .Single(value => value.Path == "gameplay/core/tie_instances.bin").Bytes;
            Equal(41f, UyaTieInstancesReader.Read(deferredTieBytes).Instances.Single().Transform.Position.X,
                "deferred tie layer preserves its last staged transform");
            await UyaBakeService.BakeAsync(project, catalog, context);

            var baseInspection = await UyaBaseLayerStore.InspectAsync(project, catalog);
            var baseManifest = baseInspection.Manifest!;
            var skyRecord = baseManifest.Layers.Single(value => value.Layer == BakeLayerId.Sky);
            var sourceSky = skyRecord.Assets.Single();
            var sourceSkyEntry = catalog.Query(new(Id: sourceSky.Asset.Id)).Single();
            var editedSky = await File.ReadAllBytesAsync(catalog.ResolveBlobPath(sourceSky.Asset.Id)!);
            Array.Resize(ref editedSky, editedSky.Length + 0x10);
            var editedSkyEntry = await catalog.PutAsync(AssetKind.Sky, sourceSky.CanonicalFormatVersion, editedSky,
                new("test-edit", sourceSkyEntry.Sources.Single(), sourceSkyEntry.Aliases, sourceSkyEntry.Tags));
            var baseManifestPath = Path.Combine(project, UyaBaseLayerStore.RelativeManifestPath);
            var originalManifestText = await File.ReadAllTextAsync(baseManifestPath);
            await File.WriteAllTextAsync(baseManifestPath,
                originalManifestText.Replace(
                    sourceSky.Asset.Id.ToString(), editedSkyEntry.Id.ToString(), StringComparison.Ordinal));
            var assetBake = await UyaBakeService.BakeAsync(project, catalog, context);
            Equal(true, assetBake.WrittenLayers.Select(value => value.Layer).SequenceEqual([BakeLayerId.Sky]),
                "resized sky rebuilds only its bake layer");
            var packedAssetEdit = await UyaLevelPackService.PackAsync(project, catalog, sourceLevelWad, context);
            Equal(true, packedAssetEdit.Succeeded, "resized asset-WAD payload packs: "
                + string.Join(" | ", packedAssetEdit.Diagnostics.Select(value => value.Cause)));
            var packedAssetFiles = UyaLevelWadRenderPackageBuilder.ReadAssetSourceFiles(
                UyaLevelWadUnpacker.Unpack(packedAssetEdit.OutputBytes!).Files);
            var packedAssetWad = BinaryMagic.IsWad(packedAssetFiles.AssetWadBytes)
                ? WadCompression.Decompress(packedAssetFiles.AssetWadBytes)
                : packedAssetFiles.AssetWadBytes;
            var packedHeader = LevelAssetReader.ReadHeader(packedAssetFiles.HeaderBytes);
            var packedMobys = LevelAssetReader.ReadModelDefinitions(
                packedAssetFiles.HeaderBytes, packedHeader.MobyModelOffset, packedHeader.MobyModelCount);
            var packedTies = LevelAssetReader.ReadModelDefinitions(
                packedAssetFiles.HeaderBytes, packedHeader.TieModelOffset, packedHeader.TieModelCount);
            var packedShrubs = LevelAssetReader.ReadShrubDefinitions(
                packedAssetFiles.HeaderBytes, packedHeader.ShrubModelOffset, packedHeader.ShrubModelCount);
            var packedOffsets = LevelAssetReader.CollectKnownAssetOffsets(
                packedHeader, packedAssetWad.Length, packedMobys, packedTies, packedShrubs, [packedHeader.SceneViewSize]);
            var packedSky = LevelAssetReader.ReadAssetSlice(packedAssetWad, packedHeader.SkyOffset, packedOffsets);
            Equal(true, packedSky.SequenceEqual(editedSky), "packed WAD preserves resized sky payload");

            workspace = await ForgeProjectWorkspace.OpenAsync(project);
            var shrub = workspace.Content.Entities.Single(value => value.Layer == "shrubs");
            workspace.UpdateTransform(shrub.EntityId, shrub.Transform with
            {
                Position = shrub.Transform.Position with { X = shrub.Transform.Position.X + 1 },
            });
            await workspace.SaveAsync();
            var beforeCancel = ManifestBytes(assetBake.Manifest);
            using (var cancellation = new CancellationTokenSource())
            {
                await ThrowsAsync<OperationCanceledException>(() => UyaBakeService.BakeAsync(
                    project,
                    catalog,
                    context,
                    progress: value =>
                    {
                        if (value.Phase == UyaBakePhase.Staging
                            && value.Layer == BakeLayerId.Shrubs
                            && value.CompletedLayers > 0)
                            cancellation.Cancel();
                        return ValueTask.CompletedTask;
                    },
                    cancellationToken: cancellation.Token));
            }
            var cancelledManifest = (await BakeStagingStore.OpenAsync(project)).Manifest;
            Equal(true, beforeCancel.SequenceEqual(ManifestBytes(cancelledManifest)),
                "cancelled bake restores prior manifest");

            var afterCancel = await UyaBakeService.BakeAsync(project, catalog, context);
            Equal(true, afterCancel.Succeeded, "cancelled bake retries successfully");
            workspace = await ForgeProjectWorkspace.OpenAsync(project);
            tie = workspace.Content.Entities.Single(value => value.Layer == "ties");
            workspace.UpdateTransform(tie.EntityId, tie.Transform with
            {
                Position = tie.Transform.Position with { Y = tie.Transform.Position.Y + 1 },
            });
            await workspace.SaveAsync();
            var lightingAsset = (await UyaBaseLayerStore.InspectAsync(project, catalog)).Manifest!.Layers
                .Single(value => value.Layer == BakeLayerId.Lighting).Assets
                .Single(value => value.Name == "directional-lights.bin");
            var lightingPath = catalog.ResolveBlobPath(lightingAsset.Asset.Id)!;
            var beforeFailure = ManifestBytes(afterCancel.Manifest);
            await ThrowsAsync<InvalidDataException>(() => UyaBakeService.BakeAsync(
                project,
                catalog,
                context,
                progress: value =>
                {
                    if (value.Phase == UyaBakePhase.Staging
                        && value.Layer == BakeLayerId.Ties
                        && value.CompletedLayers > 0
                        && File.Exists(lightingPath))
                        File.Delete(lightingPath);
                    return ValueTask.CompletedTask;
                }));
            var failedManifest = (await BakeStagingStore.OpenAsync(project)).Manifest;
            Equal(true, beforeFailure.SequenceEqual(ManifestBytes(failedManifest)),
                "failed bake restores prior manifest");

            await UyaProjectService.RepairValidatedAsync(
                project, catalog, new MemoryStream(iso, writable: false));
            var recovered = await UyaBakeService.BakeAsync(project, catalog, context);
            Equal(true, recovered.Succeeded, "failed bake repairs and retries successfully");
            var repeated = await UyaBakeService.BakeAsync(project, catalog, context);
            Equal(true, repeated.IsCurrent, "repeated bake is current");
            Equal(true, ManifestBytes(recovered.Manifest)
                .SequenceEqual(ManifestBytes(repeated.Manifest)),
                "repeated bake is manifest deterministic");

            var physicalIso = iso.ToArray();
            UyaIsoSetupTests.AddDiscIdentity(physicalIso);
            BinaryPrimitives.WriteInt32LittleEndian(
                physicalIso.AsSpan(0x500 * UyaLevelConstants.SectorSize + 8), 3);
            using var physicalStream = new MemoryStream(physicalIso, writable: false);
            var physicalSourceLevel = UyaLooseLevelWadExtractor.ExtractPrimary(physicalStream, 3).Bytes;
            var cleanIso = Path.Combine(root, "clean.iso");
            var developmentIso = Path.Combine(root, "development.iso");
            await File.WriteAllBytesAsync(cleanIso, physicalIso);
            await File.WriteAllBytesAsync(developmentIso, physicalIso);
            var cleanHash = Convert.ToHexString(SHA256.HashData(physicalIso));
            var phases = new List<UyaBuildPatchPhase>();
            var buildRequest = new UyaBuildPatchRequest(
                project, catalog.RootPath, cleanIso, developmentIso, new string('a', 32), new HashSet<string>());
            var built = await UyaBuildPatchService.RunAsync(
                buildRequest, "host-test", "sdk-test",
                progress: value =>
                {
                    phases.Add(value.Phase);
                    return ValueTask.CompletedTask;
                });
            Equal(true, built.Succeeded, "one-click build succeeds: " + string.Join(" | ", built.Diagnostics));
            Equal(developmentIso, built.DevelopmentIsoPath, "one-click build identifies development ISO");
            if (built.PatchMode == UyaIsoPatchMode.FullImageReplacement)
                Equal(true, built.NextAction.Contains("savestates retain", StringComparison.Ordinal),
                    "replacement build savestate guidance");
            Equal(true, phases.Contains(UyaBuildPatchPhase.Bake), "one-click build reports bake progress");
            Equal(true, phases.Contains(UyaBuildPatchPhase.Pack), "one-click build reports pack progress");
            Equal(true, phases.Contains(UyaBuildPatchPhase.Plan), "one-click build reports plan progress");
            Equal(true, phases.Contains(UyaBuildPatchPhase.Patch), "one-click build reports patch progress");
            Equal(true, phases.Contains(UyaBuildPatchPhase.Complete), "one-click build reports completion");
            Equal(cleanHash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(cleanIso))),
                "one-click build preserves clean ISO");
            await using (var installedIso = File.OpenRead(developmentIso))
            {
                var installedLevel = UyaLooseLevelWadExtractor.ExtractPrimary(installedIso, 3).Bytes;
                Equal(built.OutputLevelWadSha256,
                    Convert.ToHexString(SHA256.HashData(installedLevel)).ToLowerInvariant(),
                    "one-click build installs its verified WAD");
                var installedAssets = ReadAssets(installedLevel);
                foreach (var (kind, marker) in new[]
                    {
                        (AssetKind.Moby, (byte)0x11),
                        (AssetKind.Tie, (byte)0x44),
                        (AssetKind.Shrub, (byte)0x33),
                    })
                    Equal(true, UyaTextureQualification.MatchesBaseColors(
                        installedAssets.Source, installedAssets.Header, installedAssets.AssetWad, kind, marker),
                        $"installed {kind} texture preserves every color");
            }

            var noOpBuild = await UyaBuildPatchService.RunAsync(
                buildRequest, "host-test", "sdk-test");
            Equal(true, noOpBuild.Succeeded, "repeated one-click build succeeds");
            Equal(true, noOpBuild.BakeWasCurrent, "repeated one-click build skips clean layers");
            Equal(0, noOpBuild.BakedLayerCount, "repeated one-click build bakes no layers");
            Equal(built.OutputLevelWadSha256, noOpBuild.OutputLevelWadSha256,
                "repeated one-click build is byte deterministic");

            var interruptedPack = await UyaLevelPackService.PackAsync(
                project,
                catalog,
                physicalSourceLevel,
                new(workspace.Manifest.Target, "ratchet-sdk:sdk-test", "forge-host:host-test"));
            Equal(true, interruptedPack.Succeeded, "interrupted patch fixture packs");
            var interruptedPlan = await UyaIsoPatchService.PlanAsync(
                cleanIso, developmentIso, 3, interruptedPack.OutputBytes!, expectedIsoSize: physicalIso.Length);
            await ThrowsAsync<InvalidOperationException>(() => UyaIsoPatchService.ApplyAsync(
                interruptedPlan,
                progress: null,
                fault: value =>
                {
                    if (value.Phase == UyaIsoPatchFaultPhase.RangeDurable)
                        throw new InvalidOperationException("Injected interrupted patch.");
                },
                CancellationToken.None));
            phases.Clear();
            var recoveredBuild = await UyaBuildPatchService.RunAsync(
                buildRequest, "host-test", "sdk-test",
                progress: value =>
                {
                    phases.Add(value.Phase);
                    return ValueTask.CompletedTask;
                });
            Equal(true, recoveredBuild.Succeeded, "one-click build recovers an interrupted patch");
            Equal(true, phases.Contains(UyaBuildPatchPhase.Recovery), "one-click build reports recovery progress");
            Equal<UyaIsoPatchRecovery?>(null, await UyaIsoPatchService.InspectRecoveryAsync(developmentIso),
                "one-click build clears the interrupted patch journal");

            workspace = await ForgeProjectWorkspace.OpenAsync(project);
            shrub = workspace.Content.Entities.Single(value => value.Layer == "shrubs");
            workspace.UpdateTransform(shrub.EntityId, shrub.Transform with
            {
                Position = shrub.Transform.Position with { Y = shrub.Transform.Position.Y + 1 },
            });
            await workspace.SaveAsync();
            var buildPlan = await UyaBuildPatchService.PlanAsync(
                project, catalog.RootPath, "host-test", "sdk-test");
            Equal(BakeLayerState.Dirty,
                buildPlan.Layers.Single(value => value.Layer == BakeLayerId.Shrubs).State,
                "build plan reports the edited shrub layer");
            Equal(BakeLayerState.DependencyInvalidated,
                buildPlan.Layers.Single(value => value.Layer == BakeLayerId.Lighting).State,
                "build plan reports dependent lighting");
            Equal(true, buildPlan.Layers.Single(value => value.Layer == BakeLayerId.Shrubs).CanDefer,
                "previously staged layers can be deferred");
            var developmentHash = SHA256.HashData(await File.ReadAllBytesAsync(developmentIso));
            using var buildCancellation = new CancellationTokenSource();
            var cancellationError = await ThrowsAsync<OperationCanceledException>(() => UyaBuildPatchService.RunAsync(
                buildRequest,
                "host-test",
                "sdk-test",
                progress: value =>
                {
                    if (value.Phase == UyaBuildPatchPhase.Pack) buildCancellation.Cancel();
                    return ValueTask.CompletedTask;
                },
                cancellationToken: buildCancellation.Token));
            Equal(true, cancellationError.Message.Contains("development ISO was not changed", StringComparison.Ordinal),
                "cancelled one-click build reports resulting state");
            var cancelledDevelopment = await File.ReadAllBytesAsync(developmentIso);
            Equal(true, developmentHash.SequenceEqual(SHA256.HashData(cancelledDevelopment)),
                "cancelled one-click build preserves development ISO");

            workspace = await ForgeProjectWorkspace.OpenAsync(project);
            tie = workspace.Content.Entities.Single(value => value.Layer == "ties");
            workspace.RemoveEntity(tie.EntityId);
            await workspace.SaveAsync();
            var deletedTieBake = await UyaBakeService.BakeAsync(project, catalog, context);
            Equal(true, deletedTieBake.Succeeded, "tie deletion bakes");
            var deletedTiePack = await UyaLevelPackService.PackAsync(project, catalog, sourceLevelWad, context);
            Equal(true, deletedTiePack.Succeeded, "tie deletion packs: "
                + string.Join(" | ", deletedTiePack.Diagnostics.Select(value => value.Cause)));
            var deletedTieFiles = UyaLevelWadUnpacker.Unpack(deletedTiePack.OutputBytes!).Files;
            Equal(0, UyaTieInstancesReader.Read(deletedTieFiles
                .Single(value => value.Path == "gameplay/core/tie_instances.bin").Bytes).Instances.Count,
                "packed tie deletion removes the instance");
            Equal(0, UyaGameplayLightingReader.ReadTieAmbientRgbas(deletedTieFiles
                .Single(value => value.Path == "gameplay/core/tie_ambient_rgbas.bin").Bytes, 0).Length,
                "packed tie deletion removes its ambient-light entry");
            Equal(0, UyaTieGroupsReader.Read(deletedTieFiles
                .Single(value => value.Path == "gameplay/core/tie_groups.bin").Bytes).Groups.Single().Length,
                "packed tie deletion removes its group reference");
            Equal(0, UyaOcclusionMappingsReader.Read(deletedTieFiles
                .Single(value => value.Path == "gameplay/core/occlusion.bin").Bytes).Ties.Count,
                "packed tie deletion removes its occlusion mapping");
            var deletedTieGameplay = deletedTieFiles
                .Single(value => value.Path == "gameplay/gameplay_core.bin").Bytes;
            Equal(true, UyaGameplayLayout.Core.Blocks.All(block =>
            {
                var pointer = BinaryPrimitives.ReadInt32LittleEndian(deletedTieGameplay.AsSpan(block.HeaderOffset));
                return pointer == 0 || pointer % (block.SemanticName == "occlusion" ? 0x40 : 0x10) == 0;
            }), "packed tie deletion preserves gameplay pointer alignment");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task VerifyCollisionBakeWorkflowAsync(
        string root,
        AssetCatalogStore catalog,
        byte[] iso,
        byte[] sourceLevelWad,
        BakeFingerprintContext context)
    {
        var project = Path.Combine(root, "collision-bake");
        var proxyBytes = CreateCollisionProxy();
        await UyaProjectService.CreateValidatedAsync(
            new MemoryStream(iso, writable: false),
            catalog,
            new("synthetic.iso", catalog.RootPath, project, "Collision bake",
                new string('a', 32), "1.00", 3, true));
        var initialBake = await UyaBakeService.BakeAsync(project, catalog, context);
        Equal(true, initialBake.Succeeded,
            "collision qualification project initially bakes");
        var initialCollisionPath = initialBake.Manifest.Layers
            .Single(value => value.Layer == BakeLayerId.Collision).RelativePath;

        var source = UyaRenderPackageTests.BuildCollisionFixture();
        var entry = await catalog.PutAsync(
            AssetKind.Collision,
            UyaBaseLayerSchema.CanonicalFormatVersion,
            source,
            new(
                "test-collision",
                new("UYA", "NTSC-U", "1.00", "level03", "level_wad/assets/asset_wad.bin", 0,
                    new string('a', 32)),
                ["base:collision:collision"],
                ["vanilla", "game:UYA", "level:03", "base-layer"]));
        var baseLayerPath = Path.Combine(project, UyaBaseLayerStore.RelativeManifestPath);
        var baseLayers = ForgeProjectPersistence.Deserialize<UyaBaseLayerManifest>(
            await File.ReadAllBytesAsync(baseLayerPath), "test base layers");
        var collisionLayer = baseLayers.Layers.Single(value => value.Layer == BakeLayerId.Collision) with
        {
            Assets =
            [
                new("collision.bin", new(entry.Id, AssetKind.Collision), UyaBaseLayerSchema.CanonicalFormatVersion),
            ],
        };
        await File.WriteAllBytesAsync(baseLayerPath, ForgeProjectPersistence.Serialize(baseLayers with
        {
            Layers = baseLayers.Layers.Select(value => value.Layer == BakeLayerId.Collision
                ? collisionLayer
                : value).ToArray(),
        }));

        var inspection = CollisionConverter.Inspect(source, GameId.UYA);
        var workspace = await ForgeProjectWorkspace.OpenAsync(project);
        var entities = inspection.Pieces.Select(piece => new ProjectEntity(
            EntityId.New(),
            $"{(piece.Kind == CollisionPieceKind.Solid ? "Solid" : "Player barrier")} #{piece.SourcePieceIndex}",
            "collision",
            ProjectTransform.Identity,
            new(entry.Id, AssetKind.Collision),
            new("UYA", 3, "collision/primary", piece.SourcePieceIndex),
            Collision: new(
                piece.Kind == CollisionPieceKind.Solid
                    ? ProjectCollisionPieceKind.Solid
                    : ProjectCollisionPieceKind.PlayerBarrier,
                0,
                piece.SourcePieceIndex,
                piece.FaceCount,
                piece.VertexCount,
                piece.Types.Select(value => new ProjectCollisionTypeCount(value.RawType, value.Count)).ToArray())))
            .ToArray();
        foreach (var entity in entities) workspace.AddEntity(entity);
        var solid = entities.First(value => value.Collision!.Kind == ProjectCollisionPieceKind.Solid);
        var barrier = entities.Single(value => value.Collision!.Kind == ProjectCollisionPieceKind.PlayerBarrier);
        workspace.UpdateTransform(solid.EntityId, solid.Transform with { Position = new(1, 0, 0) });
        workspace.UpdateTransform(barrier.EntityId, barrier.Transform with { Position = new(0, 1, 0) });
        await workspace.SaveAsync();

        var movedBake = await UyaBakeService.BakeAsync(project, catalog, context);
        Equal(true, movedBake.WrittenLayers.Select(value => value.Layer).SequenceEqual([BakeLayerId.Collision]),
            "collision transforms rebuild only collision");
        Equal(false, Directory.Exists(ForgeProjectPersistence.ResolveRelativePath(
            Path.Combine(project, BakeStagingStore.StagingDirectoryName), initialCollisionPath)),
            "successful bake prunes replaced staging output");
        var movedPack = await UyaLevelPackService.PackAsync(project, catalog, sourceLevelWad, context);
        Equal(true, movedPack.Succeeded, "edited collision staging packs: "
            + string.Join(" | ", movedPack.Diagnostics.Select(value => value.Cause)));
        var moved = UyaBaseLayerService.Extract(UyaLevelWadUnpacker.Unpack(movedPack.OutputBytes!))
            .Single(value => value.Layer == BakeLayerId.Collision && value.Name == "collision.bin").Bytes;
        var expected = CollisionConverter.Compose(source, GameId.UYA,
        [
            new(CollisionPieceKind.Solid, 0, 1, 0, 0),
            new(CollisionPieceKind.PlayerBarrier, 0, 0, 1, 0),
        ]).Bytes;
        Equal(true, moved.SequenceEqual(expected),
            "packed WAD preserves solid and player-barrier translations");

        var linkedTie = workspace.Content.Entities.First(value =>
            value.Asset?.Kind == AssetKind.Tie && value.State?.Disabled != true);
        var linkedPiece = workspace.Content.Entities.Single(value => value.EntityId == solid.EntityId);
        workspace.RemoveEntity(linkedPiece.EntityId);
        workspace.AddEntity(linkedPiece with
        {
            Collision = linkedPiece.Collision! with
            {
                Attachment = new(linkedTie.EntityId, linkedTie.Transform),
            },
        });
        var movedLinkedTie = linkedTie.Transform with
        {
            Position = linkedTie.Transform.Position with { X = linkedTie.Transform.Position.X + 2 },
        };
        workspace.UpdateTransform(linkedTie.EntityId, movedLinkedTie);
        await workspace.SaveAsync();
        var linkedBake = await UyaBakeService.BakeAsync(project, catalog, context);
        Equal(true, linkedBake.WrittenLayers.Any(value => value.Layer == BakeLayerId.Collision),
            "moving a TIE with recovered collision rebuilds collision");
        var linkedPack = await UyaLevelPackService.PackAsync(project, catalog, sourceLevelWad, context);
        var linkedBytes = UyaBaseLayerService.Extract(UyaLevelWadUnpacker.Unpack(linkedPack.OutputBytes!))
            .Single(value => value.Layer == BakeLayerId.Collision && value.Name == "collision.bin").Bytes;
        var linkedAddition = CollisionWork.TransformAdditionRelative(
            CollisionWork.DecodeSolidPieceAddition(source, GameId.UYA, 0, "linked:test"),
            GameId.UYA,
            "linked:test",
            UyaInstancedCollisionCompositionService.ToCollisionTransform(linkedPiece.Transform),
            UyaInstancedCollisionCompositionService.ToCollisionTransform(linkedTie.Transform),
            UyaInstancedCollisionCompositionService.ToCollisionTransform(movedLinkedTie));
        var expectedLinked = CollisionConverter.Compose(source, GameId.UYA,
        [
            new(CollisionPieceKind.Solid, 0, 0, 0, 0, Remove: true),
            new(CollisionPieceKind.PlayerBarrier, 0, 0, 1, 0),
        ], [linkedAddition]).Bytes;
        Equal(true, linkedBytes.SequenceEqual(expectedLinked),
            "recovered collision preserves authored faces under the TIE transform delta");

        await workspace.ApplyInstancedCollisionProxyAsync(
            linkedTie.Asset!.Id,
            proxyBytes,
            UyaBaseLayerSchema.CanonicalFormatVersion,
            new(ProjectInstancedCollisionRecipeKind.Surface, 1, 1, 0, 0x3d));
        workspace.SetInstancedCollisionEnabled(linkedTie.EntityId, true);
        Equal(0, UyaInstancedCollisionCompositionService.BuildLinkedAdditions(
                workspace, linkedPiece.Asset!.Id, source, sourcePayloadIndex: null,
                replacementEntityId: null, CancellationToken.None).Count,
            "shared collision replaces recovered individual collision for that instance");
        workspace.SetInstancedCollisionEnabled(linkedTie.EntityId, false);
        Equal(1, UyaInstancedCollisionCompositionService.BuildLinkedAdditions(
                workspace, linkedPiece.Asset!.Id, source, sourcePayloadIndex: null,
                replacementEntityId: null, CancellationToken.None).Count,
            "individual mode restores recovered collision");
        workspace.SetInstancedCollisionEnabled(linkedTie.EntityId, null);
        Equal(1, UyaInstancedCollisionCompositionService.BuildLinkedAdditions(
                workspace, linkedPiece.Asset!.Id, source, sourcePayloadIndex: null,
                replacementEntityId: null, CancellationToken.None).Count,
            "disabled instanced collision preserves vanilla recovered collision");
        workspace.RemoveInstancedCollisionProxy(linkedTie.Asset.Id);

        workspace.UpdateTransform(linkedTie.EntityId, linkedTie.Transform);
        var attachedPiece = workspace.Content.Entities.Single(value => value.EntityId == solid.EntityId);
        workspace.RemoveEntity(attachedPiece.EntityId);
        workspace.AddEntity(attachedPiece with
        {
            Collision = attachedPiece.Collision! with { Attachment = null },
        });
        await workspace.SaveAsync();
        _ = await UyaBakeService.BakeAsync(project, catalog, context);

        workspace = await ForgeProjectWorkspace.OpenAsync(project);
        var proxySourceAssetId = workspace.Content.Entities
            .Where(value => value.Asset?.Kind == AssetKind.Tie)
            .GroupBy(value => value.Asset!.Id)
            .OrderBy(value => value.Count())
            .ThenBy(value => value.Key.ToString(), StringComparer.Ordinal)
            .First().Key;
        var proxyInstances = workspace.Content.Entities
            .Where(value => value.Asset?.Id == proxySourceAssetId && value.State?.Disabled != true)
            .OrderBy(value => value.EntityId.ToString(), StringComparer.Ordinal)
            .ToArray();
        var proxyBinding = await workspace.ApplyInstancedCollisionProxyAsync(
            proxySourceAssetId,
            proxyBytes,
            UyaBaseLayerSchema.CanonicalFormatVersion,
            new(ProjectInstancedCollisionRecipeKind.Surface, 1, 1, 0, 0x3d));
        workspace.SetInstancedCollisionEnabled(
            proxyInstances.Select(value => value.EntityId).ToArray(), true);
        await workspace.SaveAsync();
        var boundCollisionInput = (await UyaBaseLayerStore.CreateBakeInputsAsync(project, catalog))
            .Single(value => value.Id == BakeLayerId.Collision);
        Equal(true, boundCollisionInput.AssetIds.Contains(proxyBinding.ProxyAssetId),
            "collision bake input tracks the bound proxy asset");
        Equal(true, boundCollisionInput.AssetIds.Contains(proxySourceAssetId),
            "collision bake input tracks the exact parent TIE asset");
        workspace.UpdateTransform(proxyInstances[0].EntityId, proxyInstances[0].Transform with
        {
            Position = proxyInstances[0].Transform.Position with
            {
                X = proxyInstances[0].Transform.Position.X + 1,
            },
        });
        await workspace.SaveAsync();
        var movedBoundCollisionInput = (await UyaBaseLayerStore.CreateBakeInputsAsync(project, catalog))
            .Single(value => value.Id == BakeLayerId.Collision);
        Equal(false, boundCollisionInput.RelevantSettings.Span.SequenceEqual(
            movedBoundCollisionInput.RelevantSettings.Span),
            "a bound TIE transform invalidates collision");
        workspace.UpdateTransform(proxyInstances[0].EntityId, proxyInstances[0].Transform);
        await workspace.SaveAsync();

        var proxyBake = await UyaBakeService.BakeAsync(project, catalog, context);
        Equal(true, proxyBake.WrittenLayers.Select(value => value.Layer)
            .SequenceEqual([BakeLayerId.Collision]),
            "adding an instanced collision proxy rebuilds only collision: "
                + string.Join(", ", proxyBake.WrittenLayers.Select(value => value.Layer)));
        var proxyPack = await UyaLevelPackService.PackAsync(project, catalog, sourceLevelWad, context);
        Equal(true, proxyPack.Succeeded, "instanced collision staging packs");
        var proxied = UyaBaseLayerService.Extract(UyaLevelWadUnpacker.Unpack(proxyPack.OutputBytes!))
            .Single(value => value.Layer == BakeLayerId.Collision && value.Name == "collision.bin").Bytes;
        Equal(
            CollisionConverter.Inspect(expected, GameId.UYA).Pieces.Sum(value => value.FaceCount)
                + proxyInstances.Length,
            CollisionConverter.Inspect(proxied, GameId.UYA).Pieces.Sum(value => value.FaceCount),
            "bake expands one proxy face for every enabled matching TIE instance");

        var unpaintedFaces = CollisionWork.DecodeSolidAddition(proxied, GameId.UYA, "unpainted").Faces;
        workspace = await ForgeProjectWorkspace.OpenAsync(project);
        workspace.SetInstancedCollisionFaceTypes(
            proxySourceAssetId,
            proxyBinding.ProxyAssetId,
            [new(0, 0xaf)],
            faceCount: 1);
        await workspace.SaveAsync();
        var paintedCollisionInput = (await UyaBaseLayerStore.CreateBakeInputsAsync(project, catalog))
            .Single(value => value.Id == BakeLayerId.Collision);
        Equal(false, boundCollisionInput.RelevantSettings.Span.SequenceEqual(
            paintedCollisionInput.RelevantSettings.Span),
            "paint-only changes invalidate collision");
        var paintedProxyBake = await UyaBakeService.BakeAsync(project, catalog, context);
        Equal(true, paintedProxyBake.WrittenLayers.Select(value => value.Layer)
            .SequenceEqual([BakeLayerId.Collision]),
            "painting a proxy face rebuilds only collision");
        var paintedProxyPack = await UyaLevelPackService.PackAsync(project, catalog, sourceLevelWad, context);
        var paintedProxy = UyaBaseLayerService.Extract(
                UyaLevelWadUnpacker.Unpack(paintedProxyPack.OutputBytes!))
            .Single(value => value.Layer == BakeLayerId.Collision && value.Name == "collision.bin").Bytes;
        var paintedFaces = CollisionWork.DecodeSolidAddition(paintedProxy, GameId.UYA, "painted").Faces;
        Equal(unpaintedFaces.Count, paintedFaces.Count,
            "painting preserves composed collision topology");
        Equal(unpaintedFaces.Count(value => value.RawType == 0xaf) + proxyInstances.Length,
            paintedFaces.Count(value => value.RawType == 0xaf),
            "packed collision applies the painted raw type to every matching instance");
        Equal(unpaintedFaces.Count(value => value.RawType == 0x3d) - proxyInstances.Length,
            paintedFaces.Count(value => value.RawType == 0x3d),
            "painted faces no longer use the binding default");

        workspace = await ForgeProjectWorkspace.OpenAsync(project);
        workspace.SetInstancedCollisionFaceTypes(
            proxySourceAssetId,
            proxyBinding.ProxyAssetId,
            [new(0, 0x3d)],
            faceCount: 1);
        await workspace.SaveAsync();
        var resetCollisionInput = (await UyaBaseLayerStore.CreateBakeInputsAsync(project, catalog))
            .Single(value => value.Id == BakeLayerId.Collision);
        Equal(true, boundCollisionInput.RelevantSettings.Span.SequenceEqual(
            resetCollisionInput.RelevantSettings.Span),
            "reset restores the uniform collision fingerprint");
        var resetProxyBake = await UyaBakeService.BakeAsync(project, catalog, context);
        Equal(true, resetProxyBake.WrittenLayers.Select(value => value.Layer)
            .SequenceEqual([BakeLayerId.Collision]),
            "resetting a painted face rebuilds only collision");
        var resetProxyPack = await UyaLevelPackService.PackAsync(project, catalog, sourceLevelWad, context);
        var resetProxy = UyaBaseLayerService.Extract(
                UyaLevelWadUnpacker.Unpack(resetProxyPack.OutputBytes!))
            .Single(value => value.Layer == BakeLayerId.Collision && value.Name == "collision.bin").Bytes;
        Equal(true, proxied.SequenceEqual(resetProxy),
            "reset restores byte-identical uniform collision");

        workspace = await ForgeProjectWorkspace.OpenAsync(project);
        var qualifiedTransform = ProjectTransform.Identity with
        {
            Position = new(1, 2, 3),
            Rotation = new(0, 0, MathF.Sin(MathF.PI / 4), MathF.Cos(MathF.PI / 4)),
            Scale = new(-2, 1, 1),
        };
        workspace.UpdateTransform(proxyInstances[0].EntityId, qualifiedTransform);
        await workspace.SaveAsync();
        var transformedProxyBake = await UyaBakeService.BakeAsync(project, catalog, context);
        Equal(true, transformedProxyBake.WrittenLayers.Select(value => value.Layer)
            .SequenceEqual([BakeLayerId.Collision, BakeLayerId.Ties, BakeLayerId.Lighting]),
            "transforming a bound TIE rebuilds collision and its visible TIE layers");
        var transformedProxyPack = await UyaLevelPackService.PackAsync(
            project, catalog, sourceLevelWad, context);
        proxied = UyaBaseLayerService.Extract(UyaLevelWadUnpacker.Unpack(transformedProxyPack.OutputBytes!))
            .Single(value => value.Layer == BakeLayerId.Collision && value.Name == "collision.bin").Bytes;
        var expectedQualifiedFace = CollisionWork.TransformAddition(
            CollisionWork.DecodeSolidAddition(proxyBytes, GameId.UYA, "proxy"),
            GameId.UYA,
            "qualified",
            new(
                new(qualifiedTransform.Position.X, qualifiedTransform.Position.Y, qualifiedTransform.Position.Z),
                new(qualifiedTransform.Rotation.X, qualifiedTransform.Rotation.Y,
                    qualifiedTransform.Rotation.Z, qualifiedTransform.Rotation.W),
                new(qualifiedTransform.Scale.X, qualifiedTransform.Scale.Y, qualifiedTransform.Scale.Z)))
            .Faces.Single();
        var packedQualifiedFaces = CollisionWork.DecodeSolidAddition(proxied, GameId.UYA, "packed")
            .Faces.Where(value => value.RawType == 0x3d).ToArray();
        Equal(true, packedQualifiedFaces.Any(value => value.IsQuad == expectedQualifiedFace.IsQuad
                && ((value.A == expectedQualifiedFace.A && value.B == expectedQualifiedFace.B
                        && value.C == expectedQualifiedFace.C)
                    || (value.A == expectedQualifiedFace.B && value.B == expectedQualifiedFace.C
                        && value.C == expectedQualifiedFace.A)
                    || (value.A == expectedQualifiedFace.C && value.B == expectedQualifiedFace.A
                        && value.C == expectedQualifiedFace.B))),
            "packed collision applies translation, rotation, scale, and mirrored winding");
        var repeatedProxyBake = await UyaBakeService.BakeAsync(project, catalog, context);
        Equal(0, repeatedProxyBake.WrittenLayers.Count,
            "an unchanged proxy bake writes no layers");
        var repeatedProxyPack = await UyaLevelPackService.PackAsync(project, catalog, sourceLevelWad, context);
        Equal(true, transformedProxyPack.OutputBytes!.SequenceEqual(repeatedProxyPack.OutputBytes!),
            "equal proxy inputs pack byte-identically");

        workspace = await ForgeProjectWorkspace.OpenAsync(project);
        workspace.SetInstancedCollisionEnabled(proxyInstances[0].EntityId, false);
        await workspace.SaveAsync();
        var disabledProxyBake = await UyaBakeService.BakeAsync(project, catalog, context);
        Equal(true, disabledProxyBake.WrittenLayers.Select(value => value.Layer)
            .SequenceEqual([BakeLayerId.Collision]),
            "switching one proxy instance to individual mode rebuilds only collision");
        var disabledProxyPack = await UyaLevelPackService.PackAsync(project, catalog, sourceLevelWad, context);
        var disabledProxy = UyaBaseLayerService.Extract(
                UyaLevelWadUnpacker.Unpack(disabledProxyPack.OutputBytes!))
            .Single(value => value.Layer == BakeLayerId.Collision && value.Name == "collision.bin").Bytes;
        Equal(
            CollisionConverter.Inspect(proxied, GameId.UYA).Pieces.Sum(value => value.FaceCount) - 1,
            CollisionConverter.Inspect(disabledProxy, GameId.UYA).Pieces.Sum(value => value.FaceCount),
            "individual mode removes exactly that instance's shared proxy collision");

        workspace = await ForgeProjectWorkspace.OpenAsync(project);
        await workspace.ApplyInstancedCollisionProxyAsync(
            proxyInstances[0].EntityId,
            proxyBytes,
            UyaBaseLayerSchema.CanonicalFormatVersion,
            new(ProjectInstancedCollisionRecipeKind.Surface, 1, 1, 0, 0x3d));
        await workspace.SaveAsync();
        var individualProxyBake = await UyaBakeService.BakeAsync(project, catalog, context);
        Equal(true, individualProxyBake.WrittenLayers.Select(value => value.Layer)
            .SequenceEqual([BakeLayerId.Collision]),
            "adding an individual proxy rebuilds only collision");
        var individualProxyPack = await UyaLevelPackService.PackAsync(project, catalog, sourceLevelWad, context);
        var individualProxy = UyaBaseLayerService.Extract(
                UyaLevelWadUnpacker.Unpack(individualProxyPack.OutputBytes!))
            .Single(value => value.Layer == BakeLayerId.Collision && value.Name == "collision.bin").Bytes;
        Equal(
            CollisionConverter.Inspect(proxied, GameId.UYA).Pieces.Sum(value => value.FaceCount),
            CollisionConverter.Inspect(individualProxy, GameId.UYA).Pieces.Sum(value => value.FaceCount),
            "individual proxy restores collision for only that instance");
        var individualCollisionInput = (await UyaBaseLayerStore.CreateBakeInputsAsync(project, catalog))
            .Single(value => value.Id == BakeLayerId.Collision);
        workspace.SetInstancedCollisionEnabled(proxyInstances[0].EntityId, null);
        await workspace.SaveAsync();
        var disabledCollisionInput = (await UyaBaseLayerStore.CreateBakeInputsAsync(project, catalog))
            .Single(value => value.Id == BakeLayerId.Collision);
        Equal(false, individualCollisionInput.RelevantSettings.Span.SequenceEqual(
                disabledCollisionInput.RelevantSettings.Span),
            "disabling individual collision invalidates collision");

        workspace = await ForgeProjectWorkspace.OpenAsync(project);
        workspace.SetInstancedCollisionEnabled(proxyInstances[0].EntityId, true);
        await workspace.SaveAsync();
        var restoredOptOutBake = await UyaBakeService.BakeAsync(project, catalog, context);
        Equal(true, restoredOptOutBake.WrittenLayers.Select(value => value.Layer)
            .SequenceEqual([BakeLayerId.Collision]),
            "re-enabling an opted-out proxy instance rebuilds collision");

        workspace = await ForgeProjectWorkspace.OpenAsync(project);
        workspace.SetEntityState([proxyInstances[0].EntityId], hidden: null, disabled: true, locked: null);
        await workspace.SaveAsync();
        var disabledEntityBake = await UyaBakeService.BakeAsync(project, catalog, context);
        Equal(true, disabledEntityBake.WrittenLayers.Any(value => value.Layer == BakeLayerId.Collision),
            "disabling a bound TIE invalidates collision");
        var disabledEntityPack = await UyaLevelPackService.PackAsync(project, catalog, sourceLevelWad, context);
        var disabledEntityCollision = UyaBaseLayerService.Extract(
                UyaLevelWadUnpacker.Unpack(disabledEntityPack.OutputBytes!))
            .Single(value => value.Layer == BakeLayerId.Collision && value.Name == "collision.bin").Bytes;
        Equal(
            CollisionConverter.Inspect(proxied, GameId.UYA).Pieces.Sum(value => value.FaceCount) - 1,
            CollisionConverter.Inspect(disabledEntityCollision, GameId.UYA).Pieces.Sum(value => value.FaceCount),
            "disabled TIE removes exactly that instance's collision");

        workspace = await ForgeProjectWorkspace.OpenAsync(project);
        workspace.SetEntityState([proxyInstances[0].EntityId], hidden: null, disabled: false, locked: null);
        await workspace.SaveAsync();
        var restoredDisabledBake = await UyaBakeService.BakeAsync(project, catalog, context);
        Equal(true, restoredDisabledBake.WrittenLayers.Any(value => value.Layer == BakeLayerId.Collision),
            "re-enabling a disabled TIE restores its collision");

        workspace = await ForgeProjectWorkspace.OpenAsync(project);
        var removedProxyEntity = workspace.RemoveEntity(proxyInstances[0].EntityId);
        await workspace.SaveAsync();
        var deletedEntityBake = await UyaBakeService.BakeAsync(project, catalog, context);
        Equal(true, deletedEntityBake.WrittenLayers.Any(value => value.Layer == BakeLayerId.Collision),
            "deleting a bound TIE invalidates collision");
        var deletedEntityPack = await UyaLevelPackService.PackAsync(project, catalog, sourceLevelWad, context);
        var deletedEntityCollision = UyaBaseLayerService.Extract(
                UyaLevelWadUnpacker.Unpack(deletedEntityPack.OutputBytes!))
            .Single(value => value.Layer == BakeLayerId.Collision && value.Name == "collision.bin").Bytes;
        Equal(
            CollisionConverter.Inspect(proxied, GameId.UYA).Pieces.Sum(value => value.FaceCount) - 1,
            CollisionConverter.Inspect(deletedEntityCollision, GameId.UYA).Pieces.Sum(value => value.FaceCount),
            "deleted TIE removes exactly that instance's collision");

        workspace = await ForgeProjectWorkspace.OpenAsync(project);
        workspace.AddEntity(removedProxyEntity);
        await workspace.SaveAsync();
        var restoredDeletedBake = await UyaBakeService.BakeAsync(project, catalog, context);
        Equal(true, restoredDeletedBake.WrittenLayers.Any(value => value.Layer == BakeLayerId.Collision),
            "restoring a deleted TIE restores its collision");

        workspace = await ForgeProjectWorkspace.OpenAsync(project);
        workspace.RemoveInstancedCollisionProxy(proxySourceAssetId);
        await workspace.SaveAsync();
        var unboundCollisionInput = (await UyaBaseLayerStore.CreateBakeInputsAsync(project, catalog))
            .Single(value => value.Id == BakeLayerId.Collision);
        workspace.UpdateTransform(proxyInstances[0].EntityId, qualifiedTransform with
        {
            Position = qualifiedTransform.Position with
            {
                X = qualifiedTransform.Position.X + 1,
            },
        });
        await workspace.SaveAsync();
        var movedUnboundCollisionInput = (await UyaBaseLayerStore.CreateBakeInputsAsync(project, catalog))
            .Single(value => value.Id == BakeLayerId.Collision);
        Equal(true, unboundCollisionInput.RelevantSettings.Span.SequenceEqual(
            movedUnboundCollisionInput.RelevantSettings.Span),
            "an unbound TIE transform does not invalidate collision");
        workspace.UpdateTransform(proxyInstances[0].EntityId, qualifiedTransform);
        await workspace.SaveAsync();
        var removedProxyBake = await UyaBakeService.BakeAsync(project, catalog, context);
        Equal(true, removedProxyBake.WrittenLayers.Select(value => value.Layer)
            .SequenceEqual([BakeLayerId.Collision]),
            "removing a proxy binding rebuilds only collision");
        var removedProxyPack = await UyaLevelPackService.PackAsync(project, catalog, sourceLevelWad, context);
        var withoutProxy = UyaBaseLayerService.Extract(
                UyaLevelWadUnpacker.Unpack(removedProxyPack.OutputBytes!))
            .Single(value => value.Layer == BakeLayerId.Collision && value.Name == "collision.bin").Bytes;
        Equal(true, withoutProxy.SequenceEqual(expected),
            "collision bake remains byte-identical when no proxies are bound");

        workspace = await ForgeProjectWorkspace.OpenAsync(project);
        var overlapSource = workspace.Content.Entities.Single(
            value => value.EntityId == proxyInstances[0].EntityId);
        workspace.UpdateTransform(overlapSource.EntityId, ProjectTransform.Identity);
        var overlapEntity = overlapSource with
        {
            EntityId = EntityId.New(),
            Name = "Overlap fixture",
            Transform = ProjectTransform.Identity with { Position = new(0.25f, 0, 0) },
        };
        workspace.AddEntity(overlapEntity);
        var denseFaces = Enumerable.Range(0, 43).Select(index =>
        {
            var x = 0.125f + index % 7 * 0.5f;
            var y = 0.125f + index / 7 * 0.5f;
            return new CollisionSolidFace(
                0,
                new(x, y, 0.125f),
                new(x + 0.125f, y, 0.125f),
                new(x, y + 0.125f, 0.125f),
                default,
                IsQuad: false);
        }).ToArray();
        var denseProxyBytes = CollisionWork.EncodeStandalone(GameId.UYA,
            [new("dense-overlap", denseFaces)]).Bytes;
        await workspace.ApplyInstancedCollisionProxyAsync(
            proxySourceAssetId,
            denseProxyBytes,
            UyaBaseLayerSchema.CanonicalFormatVersion,
            new(ProjectInstancedCollisionRecipeKind.Surface, 1, 1, 0, 0));
        workspace.SetInstancedCollisionEnabled([overlapSource.EntityId, overlapEntity.EntityId], true);
        await workspace.SaveAsync();
        var unsafeStaging = await BakeStagingStore.OpenAsync(project);
        var safeManifest = ManifestBytes(unsafeStaging.Manifest);
        var safeCollisionLayer = unsafeStaging.Manifest.Layers
            .Single(value => value.Layer == BakeLayerId.Collision);
        var safeCollisionBytes = await File.ReadAllBytesAsync(Path.Combine(
            project,
            BakeStagingStore.StagingDirectoryName,
            safeCollisionLayer.RelativePath,
            "collision.bin"));
        var unsafeCollisionPlan = (await UyaBakeValidationService.PreflightAsync(project, catalog, context))
            .Plan.Layers.Single(value => value.Layer == BakeLayerId.Collision);
        await ThrowsAsync<InvalidDataException>(() => UyaBaseLayerStore.StageAsync(
            project, catalog, unsafeStaging, unsafeCollisionPlan));
        var preservedStaging = await BakeStagingStore.OpenAsync(project);
        Equal(true, safeManifest.SequenceEqual(ManifestBytes(preservedStaging.Manifest)),
            "combined proxy pressure failure preserves the last good manifest");
        var preservedCollisionBytes = await File.ReadAllBytesAsync(Path.Combine(
            project,
            BakeStagingStore.StagingDirectoryName,
            safeCollisionLayer.RelativePath,
            "collision.bin"));
        Equal(true, safeCollisionBytes.SequenceEqual(preservedCollisionBytes),
            "combined proxy pressure failure preserves the last good collision bytes");
        workspace.RemoveInstancedCollisionProxy(proxySourceAssetId);
        workspace.RemoveEntity(overlapEntity.EntityId);
        workspace.UpdateTransform(overlapSource.EntityId, overlapSource.Transform);
        await workspace.SaveAsync();

        var corruptionBinding = await workspace.ApplyInstancedCollisionProxyAsync(
            proxySourceAssetId,
            proxyBytes,
            UyaBaseLayerSchema.CanonicalFormatVersion,
            new(ProjectInstancedCollisionRecipeKind.Surface, 1, 1, 0, 0));
        workspace.SetInstancedCollisionEnabled(proxyInstances[0].EntityId, true);
        await workspace.SaveAsync();
        var corruptionPlan = (await UyaBakeValidationService.PreflightAsync(project, catalog, context))
            .Plan.Layers.Single(value => value.Layer == BakeLayerId.Collision);
        var corruptionStaging = await BakeStagingStore.OpenAsync(project);
        var corruptionSafeManifest = ManifestBytes(corruptionStaging.Manifest);
        var proxyPath = workspace.ResolveAssetPath(corruptionBinding.ProxyAssetId, catalog)
            ?? throw new InvalidOperationException("Collision proxy fixture disappeared.");
        await File.WriteAllBytesAsync(proxyPath, [0]);
        await ThrowsAsync<InvalidDataException>(() => UyaBaseLayerStore.StageAsync(
            project, catalog, corruptionStaging, corruptionPlan));
        var corruptProxyManifest = ManifestBytes((await BakeStagingStore.OpenAsync(project)).Manifest);
        Equal(true, corruptionSafeManifest.SequenceEqual(corruptProxyManifest),
            "corrupt proxy data preserves the last good stage");
        await File.WriteAllBytesAsync(proxyPath, proxyBytes);
        File.Delete(proxyPath);
        await ThrowsAsync<FileNotFoundException>(() => UyaBaseLayerStore.StageAsync(
            project, catalog, corruptionStaging, corruptionPlan));
        var missingProxyManifest = ManifestBytes((await BakeStagingStore.OpenAsync(project)).Manifest);
        Equal(true, corruptionSafeManifest.SequenceEqual(missingProxyManifest),
            "missing proxy data preserves the last good stage");
        await File.WriteAllBytesAsync(proxyPath, proxyBytes);
        var tiePath = workspace.ResolveAssetPath(proxySourceAssetId, catalog)
            ?? throw new InvalidOperationException("Parent TIE fixture disappeared.");
        var tieBytes = await File.ReadAllBytesAsync(tiePath);
        File.Delete(tiePath);
        await ThrowsAsync<FileNotFoundException>(() => UyaBaseLayerStore.StageAsync(
            project, catalog, corruptionStaging, corruptionPlan));
        var missingTieManifest = ManifestBytes((await BakeStagingStore.OpenAsync(project)).Manifest);
        Equal(true, corruptionSafeManifest.SequenceEqual(missingTieManifest),
            "missing parent TIE data preserves the last good stage");
        await File.WriteAllBytesAsync(tiePath, tieBytes);
        workspace.RemoveInstancedCollisionProxy(proxySourceAssetId);
        await workspace.SaveAsync();

        workspace = await ForgeProjectWorkspace.OpenAsync(project);
        workspace.UpdateTransform(solid.EntityId, solid.Transform with { Position = new(2, 0, 0) });
        await workspace.SaveAsync();
        var beforeCancellation = ManifestBytes((await BakeStagingStore.OpenAsync(project)).Manifest);
        using (var cancellation = new CancellationTokenSource())
        {
            await ThrowsAsync<OperationCanceledException>(() => UyaBakeService.BakeAsync(
                project,
                catalog,
                context,
                progress: value =>
                {
                    if (value is { Phase: UyaBakePhase.Staging, Layer: BakeLayerId.Collision, CompletedLayers: > 0 })
                        cancellation.Cancel();
                    return ValueTask.CompletedTask;
                },
                cancellationToken: cancellation.Token));
        }
        var cancelledManifest = (await BakeStagingStore.OpenAsync(project)).Manifest;
        Equal(true, beforeCancellation.SequenceEqual(ManifestBytes(cancelledManifest)),
            "cancelled collision bake preserves the last good manifest");
        var cancellationRetry = await UyaBakeService.BakeAsync(project, catalog, context);
        Equal(true, cancellationRetry.WrittenLayers.Select(value => value.Layer)
            .SequenceEqual([BakeLayerId.Collision]), "cancelled collision bake retries cleanly");

        workspace = await ForgeProjectWorkspace.OpenAsync(project);
        workspace.UpdateTransform(solid.EntityId, solid.Transform with { Position = new(3, 0, 0) });
        await workspace.SaveAsync();
        var staging = await BakeStagingStore.OpenAsync(project);
        var beforeFailure = ManifestBytes(staging.Manifest);
        var collisionPlan = (await UyaBakeValidationService.PreflightAsync(project, catalog, context))
            .Plan.Layers.Single(value => value.Layer == BakeLayerId.Collision);
        await ThrowsAsync<InvalidDataException>(() => UyaBaseLayerStore.StageAsync(
            project,
            catalog,
            staging,
            collisionPlan,
            output => File.WriteAllBytes(Path.Combine(output, "collision.bin"), [0]),
            CancellationToken.None));
        var failedManifest = (await BakeStagingStore.OpenAsync(project)).Manifest;
        Equal(true, beforeFailure.SequenceEqual(ManifestBytes(failedManifest)),
            "invalid collision output preserves the last good manifest");

        workspace = await ForgeProjectWorkspace.OpenAsync(project);
        foreach (var entity in workspace.Content.Entities
                     .Where(value => value.Collision?.Kind == ProjectCollisionPieceKind.Solid).ToArray())
            workspace.RemoveEntity(entity.EntityId);
        workspace.SetEntityState([barrier.EntityId], null, true, null);
        await workspace.SaveAsync();
        var deletedBake = await UyaBakeService.BakeAsync(project, catalog, context);
        Equal(true, deletedBake.WrittenLayers.Select(value => value.Layer).SequenceEqual([BakeLayerId.Collision]),
            "collision deletion rebuilds only collision");
        var deletedPack = await UyaLevelPackService.PackAsync(project, catalog, sourceLevelWad, context);
        Equal(true, deletedPack.Succeeded, "deleted collision staging packs");
        var deleted = UyaBaseLayerService.Extract(UyaLevelWadUnpacker.Unpack(deletedPack.OutputBytes!))
            .Single(value => value.Layer == BakeLayerId.Collision && value.Name == "collision.bin").Bytes;
        Equal(0, CollisionConverter.Inspect(deleted, GameId.UYA).Pieces.Count,
            "packed WAD removes deleted and disabled collision pieces");
    }

    private static async Task<AssetCatalogEntry> PutAsync(
        AssetCatalogStore catalog,
        AssetKind kind,
        int classId,
        byte modelByte,
        string level = "level03") =>
        await catalog.PutAsync(kind, UyaAssetImportService.CanonicalFormatVersion, CanonicalAsset(kind, modelByte), new(
            "test",
            new("UYA", "NTSC-U", "1.00", level, "level_wad/assets/asset_wad.bin", 0, new string('a', 32)),
            [$"{kind.ToString().ToLowerInvariant()}:{classId}", $"{kind.ToString().ToLowerInvariant()}:0x{classId:X4}"],
            ["vanilla", "game:UYA", $"level:{level[5..]}"]));

    private static PackedAssets ReadAssets(byte[] levelWad)
    {
        var source = UyaLevelWadRenderPackageBuilder.ReadAssetSourceFiles(
            UyaLevelWadUnpacker.Unpack(levelWad).Files);
        var assetWad = BinaryMagic.IsWad(source.AssetWadBytes)
            ? WadCompression.Decompress(source.AssetWadBytes)
            : source.AssetWadBytes;
        var header = LevelAssetReader.ReadHeader(source.HeaderBytes);
        return new(
            source,
            header,
            assetWad,
            LevelAssetReader.ReadModelDefinitions(source.HeaderBytes, header.MobyModelOffset, header.MobyModelCount),
            LevelAssetReader.ReadModelDefinitions(source.HeaderBytes, header.TieModelOffset, header.TieModelCount),
            LevelAssetReader.ReadShrubDefinitions(source.HeaderBytes, header.ShrubModelOffset, header.ShrubModelCount));
    }

    private static byte[] CanonicalAsset(AssetKind kind, byte modelByte)
    {
        var palette = new byte[0x400];
        new byte[] { modelByte, 0, 0, 128, 0, modelByte, 0, 64 }.CopyTo(palette, 0);
        var pif = PifWriter.Write(PifWriter.CreateIndexed8(2, 2, palette, [0, 1, 1, 0]));
        var definitionLength = kind == AssetKind.Shrub ? 0x30 : 0x20;
        var bytes = new byte[24 + definitionLength + pif.Length];
        "HFUYA1"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(6), definitionLength);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10 + definitionLength), 1);
        bytes[14 + definitionLength] = modelByte;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(15 + definitionLength), 1);
        bytes[19 + definitionLength] = 0;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20 + definitionLength), pif.Length);
        pif.CopyTo(bytes.AsSpan(24 + definitionLength));
        return bytes;
    }

    private static byte[] CreateCollisionProxy() => CollisionWork.EncodeStandalone(GameId.UYA,
    [
        new("test-proxy",
        [
            new(0,
                new(10, 10, 10),
                new(11, 10, 10),
                new(10, 11, 10),
                default,
                IsQuad: false),
        ]),
    ]).Bytes;

    private static byte[] ManifestBytes(BakeManifest manifest) =>
        JsonSerializer.SerializeToUtf8Bytes(manifest);

    private static ProjectEntity SkyShell(
        EntityId id,
        AssetId assetId,
        int order,
        short rotationX,
        short rotationDeltaZ) => new(
        id,
        $"Sky shell {order + 1}",
        "sky",
        ProjectTransform.Identity,
        new(assetId, AssetKind.Sky),
        new("UYA", 41, "level_wad/assets/sky", 0),
        SkyShell: new(
            0,
            order,
            new(rotationX * (MathF.PI / 32768f), 0, 0),
            new(0, 0, rotationDeltaZ * (MathF.PI / 32768f) * 60)));

    private static Skybox ReadSkybox(byte[] levelWad)
    {
        var bytes = UyaBaseLayerService.ExtractSky(UyaLevelWadUnpacker.Unpack(levelWad))!.Bytes;
        using var stream = new MemoryStream(bytes, writable: false);
        return SkyboxReader.Read(stream, GameId.UYA);
    }

    private static int[] ReadClassIds(IReadOnlyList<PackedFile> files, string family)
    {
        var bytes = files.Single(value => value.Path == $"gameplay/core/{family}_classes.bin").Bytes;
        var count = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        return Enumerable.Range(0, count)
            .Select(index => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4 + index * 4)))
            .ToArray();
    }

    private static async Task ReplaceCompressedTextAsync(string path, string oldValue, string newValue)
    {
        string text;
        await using (var input = File.OpenRead(path))
        await using (var gzip = new GZipStream(input, CompressionMode.Decompress))
        using (var reader = new StreamReader(gzip))
            text = await reader.ReadToEndAsync();
        await using var output = File.Create(path);
        await using var compressed = new GZipStream(output, CompressionLevel.Fastest);
        await using var writer = new StreamWriter(compressed);
        await writer.WriteAsync(text.Replace(oldValue, newValue, StringComparison.Ordinal));
    }

    private static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T exception) { return exception; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }

    private static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected {expected}, got {actual}");
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed record PackedAssets(
        UyaLevelAssetSourceFiles Source,
        LevelAssetHeader Header,
        byte[] AssetWad,
        IReadOnlyList<LevelAssetModelDefinition> Mobys,
        IReadOnlyList<LevelAssetModelDefinition> Ties,
        IReadOnlyList<LevelAssetShrubDefinition> Shrubs);
}
