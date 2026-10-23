using Forge.Host.Domain;
using Forge.Host.Games.UYA;

namespace Forge.Host.ProtocolTests.Games.UYA;

internal static class UyaSkyboxContentImportTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-skybox-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var catalog = await AssetCatalogStore.OpenAsync(Path.Combine(root, "catalog"));
            var targetFingerprint = new string('a', 32);
            var donorFingerprint = new string('b', 32);
            var targetBytes = global::EditorRuntimeTests.BuildSkybox();
            var donorBytes = global::EditorRuntimeTests.BuildSkybox();
            donorBytes[0x44] = 9;
            var targetPayload = new UyaBaseLayerPayload(
                BakeLayerId.Sky, "sky.bin", AssetKind.Sky,
                "level_wad/assets/asset_wad.bin", 0, targetBytes);
            var donorPayload = targetPayload with { Bytes = donorBytes };
            var targetSource = new OpaqueContentSource(
                "UYA", "NTSC-U", "1.00", 41, targetFingerprint);
            var donorSource = targetSource with { Level = 40, Fingerprint = donorFingerprint };
            var targetEntry = (await catalog.PutManyAsync(
                UyaBaseLayerService.CreateCatalogPuts([targetPayload], targetSource, "target-sky"))).Single();
            var donorEntry = (await catalog.PutManyAsync(
                UyaBaseLayerService.CreateCatalogPuts([donorPayload], donorSource, "donor-sky"))).Single();

            var targetId = EntityId.New();
            var projectPath = Path.Combine(root, "project");
            var workspace = await ForgeProjectWorkspace.CreateAsync(
                projectPath,
                "Skybox import",
                new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
                new("UYA", "NTSC-U", "1.00", 41, targetFingerprint),
                [SkyShell(targetId, targetEntry.Id, 41)]);
            await UyaBaseLayerStore.WriteAsync(
                projectPath, targetSource, [targetPayload], [targetEntry]);

            var donor = SkyShell(EntityId.New(), donorEntry.Id, 40);
            var batchId = $"import-{new string('c', 32)}";
            var family = new ContentImportFamilyPlan(
                "skybox", "Skybox", ContentImportMode.Replace,
                ContentImportMode.Replace, ContentImportSourceState.Present,
                true, false, true,
                ["source:skybox:level_wad/assets/sky:0"], [], [],
                [$"entity:{targetId}"], [], ["skybox requires shell geometry."], [],
                donorBytes.Length, [], []);
            var plan = new ContentImportPlan(
                ContentImportPlanSchema.CurrentVersion,
                ContentImportCapabilitySchema.CurrentVersion,
                batchId,
                "UYA",
                "NTSC-U",
                "1.00",
                donorFingerprint,
                new string('d', 64),
                40,
                workspace.Manifest.ProjectId.ToString(),
                workspace.CurrentFingerprint,
                41,
                donorBytes.Length,
                [family],
                [],
                []);
            var source = new UyaSkyboxContentImportSource([donor], [donorPayload]);
            var first = await UyaSkyboxContentImportService.PrepareCommitAsync(
                workspace, catalog, source, plan, 1234);
            var repeated = await UyaSkyboxContentImportService.PrepareCommitAsync(
                workspace, catalog, source, plan, 1234);
            Equal(first.Content.Entities.Single(value => value.SkyShell is not null).EntityId,
                repeated.Content.Entities.Single(value => value.SkyShell is not null).EntityId,
                "skybox import IDs are deterministic");
            Equal(1, first.Attachments.Count, "donor sky asset is attached");
            Equal(true, first.Batch.Replaced.Contains($"entity:{targetId}"),
                "target sky shell is recorded as replaced");

            await workspace.CommitContentImportAsync(first);
            var imported = workspace.Content.Entities.Single(value => value.SkyShell is not null);
            Equal(donorEntry.Id, imported.Asset!.Id, "donor sky shell becomes authoritative");
            Equal(false, workspace.Content.Entities.Any(value => value.EntityId == targetId),
                "target sky composition is removed");
            Equal(true, workspace.Content.Assets.Any(value => value.Id == donorEntry.Id),
                "donor sky remains project-attached");

            var emptyProjectPath = Path.Combine(root, "empty-project");
            var emptyWorkspace = await ForgeProjectWorkspace.CreateAsync(
                emptyProjectPath,
                "Empty skybox import",
                new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
                new("UYA", "NTSC-U", "1.00", 46, targetFingerprint),
                []);
            await UyaBaseLayerStore.WriteAsync(emptyProjectPath, targetSource with { Level = 46 }, [], []);
            var emptyFamily = family with { Remove = [] };
            var emptyPlan = plan with
            {
                TargetProjectId = emptyWorkspace.Manifest.ProjectId.ToString(),
                TargetFingerprint = emptyWorkspace.CurrentFingerprint,
                TargetLevel = 46,
                Families = [emptyFamily],
            };
            var emptyCommit = await UyaSkyboxContentImportService.PrepareCommitAsync(
                emptyWorkspace, catalog, source, emptyPlan, 1234);
            Equal(0, emptyCommit.Batch.Replaced.Count,
                "skybox replacement accepts a target with no sky shells");
            await emptyWorkspace.CommitContentImportAsync(emptyCommit);
            var emptyComposition = await UyaSkyShellEditorService.ComposeAsync(
                emptyWorkspace, catalog, emptyWorkspace.Content.Entities, CancellationToken.None);
            Equal(false, emptyComposition.IsBasePassThrough,
                "imported sky composes without a target base sky asset");
            var emptyInputs = await UyaBaseLayerStore.CreateBakeInputsAsync(
                emptyWorkspace, catalog, CancellationToken.None);
            var emptySkyInput = emptyInputs.Single(value => value.Id == BakeLayerId.Sky);
            Equal(true, emptySkyInput.AssetIds.Contains(donorEntry.Id),
                "imported sky supplies the missing sky bake base");
            Equal(0, emptySkyInput.Blockers?.Count ?? 0,
                "imported sky is buildable on a target without sky content");

            var targetOnlyCatalog = await AssetCatalogStore.OpenAsync(Path.Combine(root, "target-catalog"));
            await targetOnlyCatalog.PutManyAsync(
                UyaBaseLayerService.CreateCatalogPuts([targetPayload], targetSource, "target-only-sky"));
            var composed = await UyaSkyShellEditorService.ComposeAsync(
                workspace, targetOnlyCatalog, workspace.Content.Entities, CancellationToken.None);
            Equal(false, composed.IsBasePassThrough,
                "attached donor sky composes without its catalog entry");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ProjectEntity SkyShell(EntityId id, AssetId assetId, int level) => new(
        id,
        "Sky shell 1",
        "sky",
        ProjectTransform.Identity,
        new(assetId, AssetKind.Sky),
        new("UYA", level, "level_wad/assets/sky", 0),
        SkyShell: new(0, 0, new(0, 0, 0), new(0, 0, 0)));

    private static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected {expected}, got {actual}");
    }
}
