using System.Buffers.Binary;
using Forge.Host.Domain;
using Forge.Host.Games.UYA;
using RatchetPs2.Games.UYA.Gameplay;

namespace Forge.Host.ProtocolTests.Games.UYA;

internal static class UyaStaticContentImportTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-static-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var sourceFingerprint = new string('a', 32);
            var catalogRoot = Path.Combine(root, "catalog");
            var catalog = await AssetCatalogStore.OpenAsync(catalogRoot);
            var exact = await PutAsync(catalog, AssetKind.Tie, 0x0500, 0x11, sourceFingerprint);
            var collision = await PutAsync(catalog, AssetKind.Tie, 0x0100, 0x22, sourceFingerprint);
            var shrub = await PutAsync(catalog, AssetKind.Shrub, 0x0200, 0x33, sourceFingerprint);
            var removable = new ProjectAssetReference(
                AssetId.Compute(AssetKind.Tie, UyaStaticLayerSchema.CanonicalFormatVersion, [0x44]),
                AssetKind.Tie);
            var protectedTarget = Entity(
                "Protected exact TIE", "ties", new(exact.Id, AssetKind.Tie), 0x0100, 0, 41,
                UyaTieInstancesReader.RecordSize, new(Locked: true));
            var removableTarget = Entity(
                "Replace me", "ties", removable, 0x0300, 1, 41,
                UyaTieInstancesReader.RecordSize);
            var preservedShrub = Entity(
                "Preserved shrub", "shrubs", new(shrub.Id, AssetKind.Shrub), 0x0400, 0, 41,
                UyaShrubInstancesReader.RecordSize);
            var collisionAsset = new ProjectAssetReference(
                AssetId.Compute(AssetKind.Collision, UyaBaseLayerSchema.CanonicalFormatVersion, [0x55]),
                AssetKind.Collision);
            var attachedCollision = new ProjectEntity(
                EntityId.New(),
                "Attached collision",
                "collision",
                ProjectTransform.Identity,
                collisionAsset,
                new("UYA", 41, "collision/primary/solid", 0),
                Collision: new(ProjectCollisionPieceKind.Solid, 0, 0, 1, 3,
                    [new(0x31, 1)], [removableTarget.EntityId]));
            var sharedCollision = attachedCollision with
            {
                EntityId = EntityId.New(),
                Name = "Shared collision",
                Provenance = attachedCollision.Provenance! with { SourceIndex = 1 },
                Collision = attachedCollision.Collision! with
                {
                    SourcePieceIndex = 1,
                    LinkedInstanceEntityIds = [removableTarget.EntityId, preservedShrub.EntityId],
                },
            };
            var project = Path.Combine(root, "project");
            var workspace = await ForgeProjectWorkspace.CreateAsync(
                project,
                "Static import",
                new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
                new("UYA", "NTSC-U", "1.00", 41, sourceFingerprint),
                [protectedTarget, removableTarget, attachedCollision, sharedCollision, preservedShrub]);
            await UyaStaticLayerStore.WriteSourceAsync(project, new(
                    "UYA", "NTSC-U", "1.00", 41, sourceFingerprint),
                [
                    new(BakeLayerId.Ties, UyaTieInstancesReader.RecordSize, [0, 0, 0], []),
                    new(BakeLayerId.Shrubs, UyaShrubInstancesReader.RecordSize, [0, 0, 0], []),
                    new(BakeLayerId.Mobys, UyaMobyInstancesReader.RecordSize, [0, 0, 0], []),
                ]);

            var donorExact = Entity(
                "Donor exact TIE", "ties", new(exact.Id, AssetKind.Tie), 0x0500, 0, 40,
                UyaTieInstancesReader.RecordSize);
            var donorCollision = Entity(
                "Donor colliding TIE", "ties", new(collision.Id, AssetKind.Tie), 0x0100, 1, 40,
                UyaTieInstancesReader.RecordSize);
            var donorShrub = Entity(
                "Donor shrub", "shrubs", new(shrub.Id, AssetKind.Shrub), 0x0200, 0, 40,
                UyaShrubInstancesReader.RecordSize);
            var batchId = "import-" + new string('b', 32);
            var tiePlan = FamilyPlan(
                "ties", ContentImportMode.Replace,
                [$"entity:{removableTarget.EntityId}"], [$"entity:{protectedTarget.EntityId}"],
                [$"asset:{exact.Id}"]);
            var shrubPlan = FamilyPlan("shrubs", ContentImportMode.Add, [], [], []);
            var plan = new ContentImportPlan(
                ContentImportPlanSchema.CurrentVersion,
                ContentImportCapabilitySchema.CurrentVersion,
                batchId,
                "UYA",
                "NTSC-U",
                "1.00",
                sourceFingerprint,
                new string('c', 64),
                40,
                workspace.Manifest.ProjectId.ToString(),
                workspace.CurrentFingerprint,
                41,
                0,
                [tiePlan, shrubPlan],
                [],
                []);
            var source = new UyaStaticContentImportSource(
                [donorExact, donorCollision, donorShrub],
                [
                    new("gameplay/tie_groups", Placement(), GroupBytes(0, 1)),
                    new("gameplay/shrub_groups", Placement(), GroupBytes(0)),
                ]);

            var first = await UyaStaticContentImportService.PrepareCommitAsync(
                workspace, catalog, source, plan, 1234);
            var repeated = await UyaStaticContentImportService.PrepareCommitAsync(
                workspace, catalog, source, plan, 1234);
            Equal(true, ForgeProjectPersistence.Serialize(first.Batch).AsSpan()
                    .SequenceEqual(ForgeProjectPersistence.Serialize(repeated.Batch)),
                "static import batch is deterministic");
            Equal(first.Content.Entities.Select(EntityIdentity).ToArray(),
                repeated.Content.Entities.Select(EntityIdentity).ToArray(),
                "static import entity IDs and classes are deterministic");
            await workspace.CommitContentImportAsync(first);

            Equal(false, workspace.Content.Entities.Any(value => value.EntityId == removableTarget.EntityId),
                "replace removes an unprotected target TIE");
            Equal(false, workspace.Content.Entities.Any(value => value.EntityId == attachedCollision.EntityId),
                "replace removes collision exclusively attached to the target TIE");
            Equal(new[] { preservedShrub.EntityId }, workspace.Content.Entities
                    .Single(value => value.EntityId == sharedCollision.EntityId)
                    .Collision!.LinkedInstanceEntityIds!.ToArray(),
                "replace keeps shared collision and clears only the removed TIE link");
            Equal(true, workspace.Content.Entities.Any(value => value.EntityId == protectedTarget.EntityId),
                "replace retains a protected target TIE");
            Equal(true, workspace.Content.Entities.Any(value => value.EntityId == preservedShrub.EntityId),
                "add preserves existing target shrubs");
            var importedTies = workspace.Content.Entities.Where(value => value.Name.StartsWith("Imported Donor", StringComparison.Ordinal)
                && value.Layer == "ties").OrderBy(value => value.Provenance!.SourceIndex).ToArray();
            Equal(0x0100, importedTies[0].Source!.ClassId,
                "identical donor definition reuses the retained target class");
            Equal(UyaCustomTieClassRegistry.FirstClassId, importedTies[1].Source!.ClassId,
                "colliding donor definition receives the first deterministic custom class");
            Equal(0x0400, workspace.Content.Entities.Single(value =>
                    value.Name == "Imported Donor shrub").Source!.ClassId,
                "identical shrub definition reuses the retained target class");
            Equal(3, workspace.Content.Assets.Count, "donor definitions are attached to the project");
            Equal(2, workspace.Content.Groups.Count, "TIE and shrub group membership is imported");
            Equal(2, workspace.Content.Groups.Single(value => value.Name.Contains("ties", StringComparison.Ordinal)).Members.Count,
                "TIE group members resolve to imported stable IDs");
            Equal(1, workspace.Content.ImportBatches.Count, "import batch provenance is committed");
            Equal(new[] { $"entity:{protectedTarget.EntityId}" },
                workspace.Content.ImportBatches[0].Protected.ToArray(), "protected survivor is reported");
            Equal(true, workspace.Content.ImportBatches[0].Replaced.Contains($"entity:{attachedCollision.EntityId}"),
                "attached collision removal is recorded in batch provenance");
            await workspace.SaveAsync();

            Directory.Move(catalogRoot, Path.Combine(root, "removed-donor-catalog"));
            var emptyCatalog = await AssetCatalogStore.OpenReadOnlyAsync(Path.Combine(root, "empty-catalog"));
            var inputs = await UyaStaticLayerStore.CreateBakeInputsAsync(project, emptyCatalog);
            Equal(0, inputs.Single(value => value.Id == BakeLayerId.Ties).Blockers!.Count,
                "imported TIEs remain bakeable without donor media");
            Equal(0, inputs.Single(value => value.Id == BakeLayerId.Shrubs).Blockers!.Count,
                "imported shrubs remain bakeable without donor media");
            var bakePlan = BakeLayerGraph.CreatePlan(new(
                new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"), "translator-1", "baker-1"), inputs);
            var staging = await BakeStagingStore.OpenAsync(project);
            var tieSnapshot = await UyaStaticLayerStore.StageAsync(
                project, emptyCatalog, staging, Layer(bakePlan, BakeLayerId.Ties));
            var repeatedTie = await UyaStaticLayerStore.StageAsync(
                project, emptyCatalog, staging, Layer(bakePlan, BakeLayerId.Ties));
            Equal(tieSnapshot.OutputFingerprint, repeatedTie.OutputFingerprint,
                "imported native TIE output is deterministic");
            var baked = UyaTieInstancesReader.Read(await File.ReadAllBytesAsync(
                Path.Combine(staging.RootPath, tieSnapshot.RelativePath, "instances.bin")));
            Equal(new[] { 0x0100, 0x0100, UyaCustomTieClassRegistry.FirstClassId },
                baked.Instances.Select(value => value.ClassId).ToArray(),
                "native output rereads retained, exact, and remapped TIE instances");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static ContentImportFamilyPlan FamilyPlan(
        string family,
        ContentImportMode mode,
        IReadOnlyList<string> remove,
        IReadOnlyList<string> protect,
        IReadOnlyList<string> reuse) => new(
        family,
        family,
        ContentImportMode.AddAndReplace,
        mode,
        ContentImportSourceState.Present,
        true,
        true,
        true,
        [],
        reuse,
        [],
        remove,
        protect,
        [],
        [],
        0,
        [],
        []);

    private static ProjectEntity Entity(
        string name,
        string layer,
        ProjectAssetReference asset,
        int classId,
        int sourceIndex,
        int level,
        int recordSize,
        ProjectEntityState? state = null)
    {
        var raw = new byte[recordSize];
        BinaryPrimitives.WriteInt32LittleEndian(raw, classId);
        BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(0x1c), 1);
        BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(0x2c), 1);
        BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(0x3c), 1);
        BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(0x4c), 1);
        return new(
            EntityId.New(),
            name,
            layer,
            ProjectTransform.Identity,
            asset,
            new("UYA", level, $"gameplay/core/{layer.TrimEnd('s')}_instances", sourceIndex),
            state,
            new(classId, raw, SourceIndex: sourceIndex),
            TieLighting: layer == "ties" ? new(0, []) : null);
    }

    private static async Task<AssetCatalogEntry> PutAsync(
        AssetCatalogStore catalog,
        AssetKind kind,
        int classId,
        byte modelByte,
        string fingerprint) => await catalog.PutAsync(
        kind,
        UyaStaticLayerSchema.CanonicalFormatVersion,
        CanonicalAsset(kind, modelByte),
        new(
            "test",
            new("UYA", "NTSC-U", "1.00", "level40", "level_wad/assets/asset_wad.bin", classId, fingerprint),
            [$"{kind.ToString().ToLowerInvariant()}:{classId}"],
            ["vanilla", "game:UYA", "level:40"]));

    private static byte[] CanonicalAsset(AssetKind kind, byte modelByte)
    {
        var definitionLength = kind == AssetKind.Shrub ? 0x30 : 0x20;
        var bytes = new byte[19 + definitionLength];
        "HFUYA1"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(6), definitionLength);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10 + definitionLength), 1);
        bytes[14 + definitionLength] = modelByte;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(15 + definitionLength), 0);
        return bytes;
    }

    private static byte[] GroupBytes(params int[] members)
    {
        var bytes = new byte[0x30];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 0x10);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x10), members.Length == 0 ? -1 : 0);
        for (var index = 0; index < members.Length; index++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x20 + index * 2),
                (ushort)(members[index] | (index == members.Length - 1 ? 0x8000 : 0)));
        return bytes;
    }

    private static OpaqueSectionPlacement Placement() => new("level_wad", 0, 0, 0, 16);

    private static BakeLayerPlan Layer(BakePlan plan, BakeLayerId layer) =>
        plan.Layers.Single(value => value.Layer == layer);

    private static string EntityIdentity(ProjectEntity value) =>
        $"{value.EntityId}:{value.Source?.ClassId}:{value.Asset?.Id}";

    private static void Equal<T>(T expected, T actual, string context)
    {
        if (expected is Array expectedArray && actual is Array actualArray)
        {
            if (expectedArray.Cast<object?>().SequenceEqual(actualArray.Cast<object?>())) return;
        }
        else if (Equals(expected, actual)) return;
        throw new InvalidOperationException($"{context}: expected {Format(expected)}, got {Format(actual)}");
    }

    private static string Format<T>(T value) => value is System.Collections.IEnumerable values and not string
        ? $"[{string.Join(", ", values.Cast<object?>())}]"
        : value?.ToString() ?? "null";
}
