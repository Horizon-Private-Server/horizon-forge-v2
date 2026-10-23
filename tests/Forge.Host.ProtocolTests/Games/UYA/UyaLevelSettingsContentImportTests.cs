using Forge.Host.Domain;
using Forge.Host.Games.UYA;
using RatchetPs2.Games.UYA.Gameplay;

namespace Forge.Host.ProtocolTests.Games.UYA;

internal static class UyaLevelSettingsContentImportTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-level-settings-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var catalog = await AssetCatalogStore.OpenAsync(Path.Combine(root, "catalog"));
            var targetFingerprint = new string('a', 32);
            var donorFingerprint = new string('b', 32);
            var targetSource = new OpaqueContentSource(
                "UYA", "NTSC-U", "1.00", 41, targetFingerprint);
            var payload = new UyaBaseLayerPayload(
                BakeLayerId.World,
                "level-settings.bin",
                AssetKind.World,
                "gameplay/core/level_settings.bin",
                0,
                new byte[UyaLevelSettingsReader.MinimumSize]);
            var entry = (await catalog.PutManyAsync(
                UyaBaseLayerService.CreateCatalogPuts([payload], targetSource, "target-settings"))).Single();

            var batchId = $"import-{new string('c', 32)}";
            var targetSpline = Spline(EntityId.New(), 7, 41);
            var importedCuboidId = UyaGeometryContentImportService.ImportedEntityId(batchId, "cuboids", 3);
            var importedCuboid = Cuboid(importedCuboidId, 9, 40);
            var targetSettings = Settings(
                new(1, 2, 3),
                Reference(-1, ProjectReferences.LevelShipPath, ProjectEntityKind.Spline, null),
                Reference(-1, ProjectReferences.LevelShipCameraCuboidStart, ProjectEntityKind.Cuboid, null),
                Reference(-1, ProjectReferences.LevelShipCameraCuboidEnd, ProjectEntityKind.Cuboid, null));
            var projectPath = Path.Combine(root, "project");
            var workspace = await ForgeProjectWorkspace.CreateAsync(
                projectPath,
                "Level settings import",
                new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
                new("UYA", "NTSC-U", "1.00", 41, targetFingerprint),
                [targetSpline, importedCuboid],
                targetSettings);
            await UyaBaseLayerStore.WriteAsync(projectPath, targetSource, [payload], [entry]);

            var donorSpline = Spline(EntityId.New(), 5, 40);
            var donorCuboid = Cuboid(EntityId.New(), 3, 40);
            var donorSettings = Settings(
                new(9, 8, 7),
                Reference(5, ProjectReferences.LevelShipPath, ProjectEntityKind.Spline, donorSpline.EntityId),
                Reference(3, ProjectReferences.LevelShipCameraCuboidStart,
                    ProjectEntityKind.Cuboid, donorCuboid.EntityId),
                Reference(3, ProjectReferences.LevelShipCameraCuboidEnd,
                    ProjectEntityKind.Cuboid, donorCuboid.EntityId));
            var family = new ContentImportFamilyPlan(
                "levelSettings", "Level settings", ContentImportMode.Replace,
                ContentImportMode.Replace, ContentImportSourceState.Present,
                true, false, true,
                ["source:levelSettings:singleton"], [], [], ["settings:project"], [], [], [],
                payload.Bytes.Length, [], []);
            var requirement = new ContentImportReferenceRequirement(
                "levelSettings", -1, ProjectReferences.LevelShipPath, 5,
                ProjectEntityKind.Spline, true, [targetSpline.EntityId]);
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
                payload.Bytes.Length,
                [family],
                [],
                [],
                [requirement]);
            var source = new UyaLevelSettingsContentImportSource(
                donorSettings, [donorSpline, donorCuboid]);
            await ThrowsAsync<InvalidDataException>(() =>
                UyaLevelSettingsContentImportService.PrepareCommitAsync(
                    workspace, catalog, source, plan, []));
            var choice = new UyaMobyExternalReferenceChoice(
                -1,
                ProjectReferences.LevelShipPath,
                5,
                UyaMobyExternalReferenceAction.Map,
                targetSpline.EntityId);
            var command = await UyaLevelSettingsContentImportService.PrepareCommitAsync(
                workspace, catalog, source, plan, [choice], 1234);
            Equal(new ProjectRgb24(9, 8, 7), command.Content.LevelSettings!.BackgroundColor,
                "donor environment settings replace the target");
            Equal(targetSpline.EntityId, command.Content.LevelSettings.Scene!.ShipPath.Reference.EntityId,
                "explicit retained spline mapping is applied");
            Equal(importedCuboidId,
                command.Content.LevelSettings.Scene.ShipCameraCuboidStart.Reference.EntityId,
                "selected donor cuboid mapping is automatic");
            Equal(importedCuboidId,
                command.Content.LevelSettings.Scene.ShipCameraCuboidEnd.Reference.EntityId,
                "repeated donor cuboid mapping remains stable");
            Equal(true, command.Batch.Replaced.Contains("settings:project"),
                "target settings are recorded as replaced");

            await workspace.CommitContentImportAsync(command);
            Equal(new ProjectRgb24(9, 8, 7), workspace.Content.LevelSettings!.BackgroundColor,
                "level-settings replacement commits atomically");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ProjectLevelSettings Settings(
        ProjectRgb24 background,
        ProjectIndexedReference shipPath,
        ProjectIndexedReference cameraStart,
        ProjectIndexedReference cameraEnd) => new(
        background,
        new(4, 5, 6),
        10,
        175,
        255,
        0,
        new(-64, false, new(1, 2, 3), new(4, 5, 6), 0.5f,
            shipPath, cameraStart, cameraEnd));

    private static ProjectIndexedReference Reference(
        int sourceValue,
        string fieldKey,
        ProjectEntityKind kind,
        EntityId? entityId) => new(sourceValue, new(
        ProjectReferenceDomain.Entity, fieldKey, true, EntityKind: kind, EntityId: entityId));

    private static ProjectEntity Spline(EntityId id, int sourceIndex, int level) => new(
        id,
        "Spline",
        "splines",
        ProjectTransform.Identity,
        null,
        new("UYA", level, "gameplay/core/splines", sourceIndex),
        Geometry: new(Spline: new([])));

    private static ProjectEntity Cuboid(EntityId id, int sourceIndex, int level) => new(
        id,
        "Cuboid",
        "cuboids",
        ProjectTransform.Identity,
        null,
        new("UYA", level, "gameplay/core/cuboids", sourceIndex),
        Geometry: new(Cuboid: new(
            Enumerable.Repeat(0f, 16).ToArray(),
            Enumerable.Repeat(0f, 12).ToArray(),
            new(0, 0, 0))));

    private static async Task ThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected {expected}, got {actual}");
    }
}
