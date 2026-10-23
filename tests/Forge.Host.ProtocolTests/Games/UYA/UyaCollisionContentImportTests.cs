using Forge.Host.Domain;
using Forge.Host.Games.UYA;
using RatchetPs2.Core.Games;
using RatchetPs2.Sdk;

namespace Forge.Host.ProtocolTests.Games.UYA;

internal static class UyaCollisionContentImportTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-collision-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var catalog = await AssetCatalogStore.OpenAsync(Path.Combine(root, "catalog"));
            var targetBytes = UyaRenderPackageTests.BuildCollisionFixture();
            var targetInspection = CollisionConverter.Inspect(targetBytes, GameId.UYA);
            var donorBytes = CollisionWork.Compose(targetBytes, GameId.UYA,
            [
                new(CollisionPieceKind.Solid, 0, 4, 0, 0),
                new(CollisionPieceKind.Solid, 1, 4, 0, 0),
                new(CollisionPieceKind.PlayerBarrier, 0, 4, 0, 0),
            ], []).Bytes;
            var donorInspection = CollisionConverter.Inspect(donorBytes, GameId.UYA);
            var targetFingerprint = new string('a', 32);
            var donorFingerprint = new string('b', 32);
            var batchId = $"import-{new string('c', 32)}";
            var targetSource = new OpaqueContentSource("UYA", "NTSC-U", "1.00", 46, targetFingerprint);
            var donorSource = targetSource with { Level = 5, Fingerprint = donorFingerprint };
            var targetPayload = new UyaBaseLayerPayload(
                BakeLayerId.Collision, "collision.bin", AssetKind.Collision,
                "level_wad/assets/asset_wad.bin", 0, targetBytes);
            var donorPayload = targetPayload with { Bytes = donorBytes };
            var targetEntry = (await catalog.PutManyAsync(
                UyaBaseLayerService.CreateCatalogPuts([targetPayload], targetSource, "target-collision"))).Single();
            var donorEntry = (await catalog.PutManyAsync(
                UyaBaseLayerService.CreateCatalogPuts([donorPayload], donorSource, "donor-collision"))).Single();
            var projectPath = Path.Combine(root, "project");
            var targetEntities = Entities(targetInspection, targetEntry.Id, 46);
            var tieAsset = AssetId.Compute(AssetKind.Tie, 1, [9]);
            var donorTie = new ProjectEntity(
                EntityId.New(), "Donor TIE", "ties", ProjectTransform.Identity,
                new(tieAsset, AssetKind.Tie),
                new("UYA", 5, "gameplay/core/tie_instances", 7),
                Source: new(0x1234, [1]));
            var importedTie = donorTie with
            {
                EntityId = UyaStaticContentImportService.ImportedEntityId(batchId, "ties", donorTie),
                Name = "Imported Donor TIE",
            };
            var workspace = await ForgeProjectWorkspace.CreateAsync(
                projectPath,
                "Collision import",
                new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
                new("UYA", "NTSC-U", "1.00", 46, targetFingerprint),
                targetEntities.Append(importedTie).ToArray());
            await UyaBaseLayerStore.WriteAsync(projectPath, targetSource, [targetPayload], [targetEntry]);

            var donorEntities = Entities(donorInspection, donorEntry.Id, 5).ToArray();
            donorEntities[0] = donorEntities[0] with
            {
                Collision = donorEntities[0].Collision! with
                {
                    LinkedInstanceEntityIds = [donorTie.EntityId],
                },
            };
            var family = new ContentImportFamilyPlan(
                "collision", "Collision", ContentImportMode.AddAndReplace,
                ContentImportMode.Add, ContentImportSourceState.Present,
                true, false, true,
                donorEntities.Select(value => $"source:collision:{value.Provenance!.Section}:"
                    + value.Provenance.SourceIndex).ToArray(),
                [], [], [], [], [], [], donorBytes.Length, [], []);
            var plan = new ContentImportPlan(
                ContentImportPlanSchema.CurrentVersion,
                ContentImportCapabilitySchema.CurrentVersion,
                batchId,
                "UYA",
                "NTSC-U",
                "1.00",
                donorFingerprint,
                new string('d', 64),
                5,
                workspace.Manifest.ProjectId.ToString(),
                workspace.CurrentFingerprint,
                46,
                donorBytes.Length,
                [family],
                [],
                []);
            var source = new UyaCollisionContentImportSource(
                donorEntities.Append(donorTie).ToArray(), [donorPayload]);
            var command = await UyaCollisionContentImportService.PrepareCommitAsync(
                workspace, catalog, source, plan, 1234);
            Equal(donorEntities.Length, command.Content.Entities.Count - targetEntities.Count - 1,
                "add imports every donor collision component");
            Equal(importedTie.EntityId, command.Content.Entities.Single(value =>
                    value.Collision?.LinkedInstanceEntityIds is { Count: > 0 })
                .Collision!.LinkedInstanceEntityIds!.Single(),
                "collision maps its explicit link to a static family imported earlier in the batch");
            Equal(1, command.Attachments.Count, "donor collision payload is attached");
            await workspace.CommitContentImportAsync(command);

            var record = (await UyaBaseLayerStore.InspectAsync(
                    workspace, catalog, BakeLayerId.Collision, CancellationToken.None))
                .Manifest!.Layers.Single(value => value.Layer == BakeLayerId.Collision);
            var composed = (await UyaBaseLayerStore.ComposeCollisionAsync(
                workspace, catalog, record, CancellationToken.None))["collision.bin"];
            var inspection = CollisionConverter.Inspect(composed, GameId.UYA);
            Equal(targetInspection.Pieces.Count(value => value.Kind == CollisionPieceKind.Solid)
                + donorInspection.Pieces.Count(value => value.Kind == CollisionPieceKind.Solid),
                inspection.Pieces.Count(value => value.Kind == CollisionPieceKind.Solid),
                "add composes donor solid pieces");
            Equal(targetInspection.Pieces.Count(value => value.Kind == CollisionPieceKind.PlayerBarrier)
                + donorInspection.Pieces.Count(value => value.Kind == CollisionPieceKind.PlayerBarrier),
                inspection.Pieces.Count(value => value.Kind == CollisionPieceKind.PlayerBarrier),
                "add composes donor player barriers");

            var attachedOnly = await AssetCatalogStore.OpenAsync(Path.Combine(root, "attached-only"));
            await attachedOnly.PutManyAsync(
                UyaBaseLayerService.CreateCatalogPuts([targetPayload], targetSource, "target-only-collision"));
            _ = await UyaBaseLayerStore.ComposeCollisionAsync(
                workspace, attachedOnly, record, CancellationToken.None);

            var removal = UyaContentImportRemovalService.Prepare(
                workspace.CreateProjection(), batchId);
            Equal(donorEntities.Length, removal.Plan.Delete.Count(value =>
                value.StartsWith("entity:", StringComparison.Ordinal)),
                "add-mode removal owns every imported collision component");
            Equal(targetEntities.Count + 1, removal.Content.Entities.Count,
                "add-mode removal restores the target collision entities");

            var replacePath = Path.Combine(root, "replace-project");
            var replaceWorkspace = await ForgeProjectWorkspace.CreateAsync(
                replacePath,
                "Collision replace import",
                new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
                new("UYA", "NTSC-U", "1.00", 46, targetFingerprint),
                targetEntities.Append(importedTie).ToArray());
            await UyaBaseLayerStore.WriteAsync(replacePath, targetSource, [targetPayload], [targetEntry]);
            var replaceFamily = family with
            {
                Mode = ContentImportMode.Replace,
                Remove = targetEntities.Select(value => $"entity:{value.EntityId}").ToArray(),
            };
            var replacePlan = plan with
            {
                BatchId = $"import-{new string('e', 32)}",
                TargetProjectId = replaceWorkspace.Manifest.ProjectId.ToString(),
                TargetFingerprint = replaceWorkspace.CurrentFingerprint,
                Families = [replaceFamily],
            };
            var replace = await UyaCollisionContentImportService.PrepareCommitAsync(
                replaceWorkspace, catalog, source, replacePlan, 1234);
            Equal(donorEntities.Length + 1, replace.Content.Entities.Count,
                "replace swaps the complete target collision family");
            Equal(targetEntities.Count, replace.Batch.Replaced.Count,
                "replace records every displaced target collision component");
            await replaceWorkspace.CommitContentImportAsync(replace);
            var replaceRecord = (await UyaBaseLayerStore.InspectAsync(
                    replaceWorkspace, catalog, BakeLayerId.Collision, CancellationToken.None))
                .Manifest!.Layers.Single(value => value.Layer == BakeLayerId.Collision);
            var replacedBytes = (await UyaBaseLayerStore.ComposeCollisionAsync(
                replaceWorkspace, catalog, replaceRecord, CancellationToken.None))["collision.bin"];
            Equal(true, replacedBytes.SequenceEqual(donorBytes),
                "unedited replacement preserves the donor's native collision octants");

            var mixedPath = Path.Combine(root, "mixed-replace-project");
            var mixedWorkspace = await ForgeProjectWorkspace.CreateAsync(
                mixedPath,
                "Collision replace with retained target piece",
                new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
                new("UYA", "NTSC-U", "1.00", 46, targetFingerprint),
                targetEntities.Append(importedTie).ToArray());
            await UyaBaseLayerStore.WriteAsync(mixedPath, targetSource, [targetPayload], [targetEntry]);
            var mixedPlan = replacePlan with
            {
                BatchId = $"import-{new string('f', 32)}",
                TargetProjectId = mixedWorkspace.Manifest.ProjectId.ToString(),
                TargetFingerprint = mixedWorkspace.CurrentFingerprint,
                Families =
                [
                    replaceFamily with
                    {
                        Remove = targetEntities.Skip(1)
                            .Select(value => $"entity:{value.EntityId}").ToArray(),
                        Protect = [$"entity:{targetEntities[0].EntityId}"],
                    },
                ],
            };
            var mixed = await UyaCollisionContentImportService.PrepareCommitAsync(
                mixedWorkspace, catalog, source, mixedPlan, 1234);
            Equal(true, mixed.Content.Entities.Any(value => value.EntityId == targetEntities[0].EntityId),
                "replace retains protected target collision beside donor collision");
            await mixedWorkspace.CommitContentImportAsync(mixed);
            var mixedRecord = (await UyaBaseLayerStore.InspectAsync(
                    mixedWorkspace, catalog, BakeLayerId.Collision, CancellationToken.None))
                .Manifest!.Layers.Single(value => value.Layer == BakeLayerId.Collision);
            _ = await UyaBaseLayerStore.ComposeCollisionAsync(
                mixedWorkspace, catalog, mixedRecord, CancellationToken.None);

            var overlap = $"entity:{targetEntities[0].EntityId}";
            var collisionOnly = UyaContentImportApplyService.Subplan(
                replacePlan with
                {
                    Families =
                    [
                        replaceFamily with { Family = "ties", Remove = [overlap] },
                        replaceFamily with { Remove = [overlap, "entity:still-present"] },
                    ],
                },
                replaceWorkspace.CurrentFingerprint,
                new HashSet<string>(["collision"], StringComparer.Ordinal),
                new HashSet<string>([overlap], StringComparer.Ordinal));
            Equal(true, collisionOnly.Families.Single(value => value.Family == "collision")
                    .Remove.SequenceEqual(["entity:still-present"]),
                "combined import does not remove static-owned collision twice");
            Equal(false, collisionOnly.Families.Single(value => value.Family == "ties").Selected,
                "collision subplan keeps earlier static families deselected");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static IReadOnlyList<ProjectEntity> Entities(
        CollisionInspection inspection,
        AssetId assetId,
        int level) => inspection.Pieces.Select(value => new ProjectEntity(
            EntityId.New(),
            $"{value.Kind} #{value.SourcePieceIndex}",
            "collision",
            ProjectTransform.Identity,
            new(assetId, AssetKind.Collision),
            new("UYA", level, $"collision/primary/{value.Kind.ToString().ToLowerInvariant()}",
                value.SourcePieceIndex),
            Collision: new(
                value.Kind == CollisionPieceKind.Solid
                    ? ProjectCollisionPieceKind.Solid
                    : ProjectCollisionPieceKind.PlayerBarrier,
                0,
                value.SourcePieceIndex,
                value.FaceCount,
                value.VertexCount,
                value.Types.Select(type => new ProjectCollisionTypeCount(type.RawType, type.Count)).ToArray())))
            .ToArray();

    private static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected {expected}, got {actual}");
    }
}
