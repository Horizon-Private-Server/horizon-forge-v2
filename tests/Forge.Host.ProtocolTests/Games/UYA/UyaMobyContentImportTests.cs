using System.Buffers.Binary;
using Forge.Host.Domain;
using Forge.Host.Games.UYA;
using RatchetPs2.Core.Gameplay;
using RatchetPs2.Games.UYA.Gameplay;

namespace Forge.Host.ProtocolTests.Games.UYA;

internal static class UyaMobyContentImportTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-moby-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var fingerprint = new string('a', 32);
            var catalogRoot = Path.Combine(root, "catalog");
            var catalog = await AssetCatalogStore.OpenAsync(catalogRoot);
            var targetAsset = Asset(0x10);
            var donorCollision = await PutAsync(catalog, 0x0100, 0x20, fingerprint);
            var donorFree = await PutAsync(catalog, 0x0200, 0x30, fingerprint);
            var reserved = new ProjectCustomClassRegistry(
                ProjectCustomClassSchema.CurrentVersion,
                ProjectCustomClassSchema.CurrentAllocationVersion,
                [new(targetAsset, UyaCustomTieClassRegistry.FirstClassId,
                    new(ProjectCustomClassOrigin.DonorMap, 0x0100))]);
            var allocation = UyaCustomTieClassRegistry.RebaseMobyDonor(
                reserved,
                [new(UyaCustomTieClassRegistry.FirstClassId, donorCollision.Id)],
                []);
            Equal(UyaCustomTieClassRegistry.FirstClassId + 1, allocation.Remaps.Single().TargetClassId,
                "orphaned Moby allocations still reserve their native class");
            var retainedTarget = Moby("Retained target", targetAsset, 0x0100, 0, 41, 7, -1, 0);
            var removableTarget = Moby("Removable target", targetAsset, 0x0300, 1, 41, 9, -1);
            var targetCuboid = new ProjectEntity(
                EntityId.New(), "Target cuboid", "cuboids", ProjectTransform.Identity, null,
                new("UYA", 41, "gameplay/core/cuboids", 0),
                Geometry: new(Cuboid: new(
                    [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1],
                    [1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0],
                    new(0, 0, 0))));
            var project = Path.Combine(root, "project");
            var workspace = await ForgeProjectWorkspace.CreateAsync(
                project,
                "Moby import",
                new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
                new("UYA", "NTSC-U", "1.00", 41, fingerprint),
                [retainedTarget, removableTarget, targetCuboid]);
            await workspace.SetSoundsAsync(ProjectSoundState.Empty with
            {
                MobyRemaps = [new(0, 0x0300, removableTarget.Asset, 0, new(0, 0), [])],
            });
            await UyaStaticLayerStore.WriteSourceAsync(project, new(
                    "UYA", "NTSC-U", "1.00", 41, fingerprint),
                [
                    new(BakeLayerId.Ties, UyaTieInstancesReader.RecordSize, [0, 0, 0], []),
                    new(BakeLayerId.Shrubs, UyaShrubInstancesReader.RecordSize, [0, 0, 0], []),
                    new(BakeLayerId.Mobys, UyaMobyInstancesReader.RecordSize, [0, 0, 0], []),
                ]);
            await OpaqueContentStore.WriteAsync(
                project,
                new("UYA", "NTSC-U", "1.00", 41, fingerprint),
                [Capture("gameplay/moby_groups", UyaTieGroupsWriter.Write(
                    new IReadOnlyList<int>[] { [0] }))]);

            var first = Moby("Donor collision", donorCollision, 0x0100, 0, 40, 7, 0, 0);
            var second = Moby("Donor free", donorFree, 0x0200, 1, 40, 8, -1);
            var donorCuboidId = EntityId.New();
            var pvarData = new byte[12];
            BinaryPrimitives.WriteInt32LittleEndian(pvarData, 1);
            BinaryPrimitives.WriteInt32LittleEndian(pvarData.AsSpan(4), 3);
            pvarData[8] = 0x5a;
            var donorPvars = new ProjectMobyPVarState(ProjectMobyPVarSchema.CurrentVersion, 1,
            [
                new(first.EntityId, 0, pvarData, pvarData.ToArray(), [0], [],
                [
                    new(0, 4, -1, 1, new(
                        ProjectReferenceDomain.Entity,
                        "target",
                        true,
                        ProjectEntityKind.Moby,
                        second.EntityId)),
                    new(4, 4, -1, 3, new(
                        ProjectReferenceDomain.Entity,
                        "trigger.cuboid",
                        true,
                        ProjectEntityKind.Cuboid,
                        donorCuboidId)),
                ]),
            ]);
            var donorGroups = new[]
            {
                Capture("gameplay/moby_groups", UyaTieGroupsWriter.Write(
                    new IReadOnlyList<int>[] { [0] })),
            };
            var source = new UyaMobyContentImportSource(
                [first, second], donorPvars, ProjectSoundState.Empty, donorGroups);
            Equal(0, UyaMobyContentImportService.CapabilityBlockers(
                    [first, second], donorPvars, donorGroups).Count,
                "safe Moby dependency closure is selectable");
            var referenceRequirements = UyaMobyContentImportService.ExternalReferenceRequirements(
                [first, second], donorPvars);
            Equal(ProjectEntityKind.Cuboid,
                referenceRequirements.Single().TargetKind,
                "typed cross-family dependency is exposed as an import requirement");

            var batchId = "import-" + new string('b', 32);
            var plan = Plan(workspace, fingerprint, batchId,
                removableTarget.EntityId, retainedTarget.EntityId);
            plan = UyaContentImportPlanningService.AddExternalReferenceRequirements(
                plan, referenceRequirements, workspace.Content);
            var plannedReference = plan.ReferenceRequirements!.Single();
            Equal(targetCuboid.EntityId, plannedReference.CandidateEntityIds.Single(),
                "plan exposes the compatible retained mapping target");
            var dex = new MobyDexCatalog(new("tests.empty", 1, []));
            var choices = new[]
            {
                new UyaMobyExternalReferenceChoice(
                    0, "trigger.cuboid", 3, UyaMobyExternalReferenceAction.Map, targetCuboid.EntityId),
            };
            await ThrowsAsync<InvalidDataException>(() => UyaMobyContentImportService.PrepareCommitAsync(
                workspace, catalog, source, plan, 1234, dex),
                "external PVar references require an explicit choice");
            var cleared = await UyaMobyContentImportService.PrepareCommitAsync(
                workspace, catalog, source, plan, 1234, dex,
                [new(0, "trigger.cuboid", 3, UyaMobyExternalReferenceAction.Clear)]);
            var clearedTables = UyaMobyPVarService.Rebuild(
                cleared.Content, GameplayPvarTableWriter.Write([]), new List<string>());
            Equal(-1, BinaryPrimitives.ReadInt32LittleEndian(clearedTables.Entries.Single().Data.AsSpan(4)),
                "nullable cross-family PVar references can be explicitly cleared");
            var firstCommit = await UyaMobyContentImportService.PrepareCommitAsync(
                workspace, catalog, source, plan, 1234, dex, choices);
            var repeated = await UyaMobyContentImportService.PrepareCommitAsync(
                workspace, catalog, source, plan, 1234, dex, choices);
            Equal(firstCommit.Content.Entities.Select(Identity).ToArray(),
                repeated.Content.Entities.Select(Identity).ToArray(),
                "Moby import IDs and native identities are deterministic");
            await workspace.CommitContentImportAsync(firstCommit);

            Equal(false, workspace.Content.Entities.Any(value => value.EntityId == removableTarget.EntityId),
                "replace removes the unprotected target Moby");
            Equal(true, workspace.Content.Entities.Any(value => value.EntityId == retainedTarget.EntityId),
                "replace preserves the protected target Moby");
            var imported = workspace.Content.Entities.Where(value =>
                    value.Name.StartsWith("Imported ", StringComparison.Ordinal))
                .OrderBy(value => value.Source!.SourceIndex).ToArray();
            Equal(new[] { UyaCustomTieClassRegistry.FirstClassId, 0x0200 },
                imported.Select(value => value.Source!.ClassId).ToArray(),
                "only the colliding Moby class is rebased");
            Equal(2, imported.Select(value => UyaMobyInstancesReader.ReadInstance(value.Source!.RawRecord).Uid)
                    .Distinct().Count(),
                "imported Moby UIDs are unique");
            Equal(2, workspace.Content.Assets.Count, "Moby definitions are attached to the project");
            Equal(1, workspace.Content.CustomClasses.Allocations.Count,
                "the colliding definition owns one custom class allocation");
            Equal(false, workspace.Content.Sounds.MobyRemaps.Any(value => value.ClassId == 0x0300),
                "replace removes the unreferenced target Moby sound map");
            var importedPvar = workspace.Content.MobyPVars!.Entries.Single();
            Equal((byte)0x5a, importedPvar.Data[8], "opaque PVar bytes remain exact");
            Equal(imported[1].EntityId,
                importedPvar.References.Single(value => value.Reference.FieldKey == "target").Reference.EntityId,
                "internal Moby PVar link resolves to the imported stable ID");
            Equal(targetCuboid.EntityId,
                importedPvar.References.Single(value =>
                    value.Reference.FieldKey == "trigger.cuboid").Reference.EntityId,
                "cross-family PVar map resolves to the retained stable ID");
            Equal(1, UyaMobyInstancesReader.ReadInstance(imported[0].Source!.RawRecord).Group,
                "colliding donor Moby group is rebased");
            Equal(new[] { imported[0].EntityId }, workspace.Content.Groups.Single().Members.ToArray(),
                "donor Moby group closure is represented in the project");

            workspace.RemoveEntity(retainedTarget.EntityId);
            var blockers = new List<string>();
            var rebuiltPvars = UyaMobyPVarService.Rebuild(
                workspace.Content, GameplayPvarTableWriter.Write([]), blockers);
            Equal(0, blockers.Count, "imported PVars rebuild without blockers");
            Equal(1, BinaryPrimitives.ReadInt32LittleEndian(rebuiltPvars.Entries.Single().Data),
                "native PVar link resolves after final Moby ordering");
            Equal(0, BinaryPrimitives.ReadInt32LittleEndian(rebuiltPvars.Entries.Single().Data.AsSpan(4)),
                "cross-family PVar link resolves after final cuboid ordering");

            await workspace.SaveAsync();
            Directory.Move(catalogRoot, Path.Combine(root, "removed-donor-catalog"));
            var emptyCatalog = await AssetCatalogStore.OpenReadOnlyAsync(Path.Combine(root, "empty-catalog"));
            var inputs = await UyaStaticLayerStore.CreateBakeInputsAsync(project, emptyCatalog);
            Equal(0, inputs.Single(value => value.Id == BakeLayerId.Mobys).Blockers!.Count,
                "imported Mobys remain bakeable without donor media");
            var bakePlan = BakeLayerGraph.CreatePlan(new(
                new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"), "translator-1", "baker-1"), inputs);
            var staging = await BakeStagingStore.OpenAsync(project);
            var snapshot = await UyaStaticLayerStore.StageAsync(
                project, emptyCatalog, staging,
                bakePlan.Layers.Single(value => value.Layer == BakeLayerId.Mobys));
            var baked = UyaMobyInstancesReader.Read(await File.ReadAllBytesAsync(
                Path.Combine(staging.RootPath, snapshot.RelativePath, "instances.bin")));
            Equal(new[] { UyaCustomTieClassRegistry.FirstClassId, 0x0200 },
                baked.Instances.Select(value => value.ClassId).ToArray(),
                "native output rereads the rebased and preserved Moby classes");
            var bakedGroups = UyaTieGroupsReader.Read(await File.ReadAllBytesAsync(
                Path.Combine(staging.RootPath, snapshot.RelativePath, "groups.bin")));
            Equal(new[] { 0 }, bakedGroups.Groups[1],
                "native output includes the imported Moby group in final instance order");

            var blocked = UyaMobyContentImportService.CapabilityBlockers(
                [first, second], donorPvars,
                [Capture("gameplay/moby_groups", UyaTieGroupsWriter.Write(
                    new IReadOnlyList<int>[] { [1] }))]);
            Equal(true, blocked.Any(value => value.Contains("groups are malformed", StringComparison.Ordinal)),
                "inconsistent native Moby groups remain explicitly gated");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static ContentImportPlan Plan(
        ForgeProjectWorkspace workspace,
        string fingerprint,
        string batchId,
        EntityId remove,
        EntityId protect) => new(
        ContentImportPlanSchema.CurrentVersion,
        ContentImportCapabilitySchema.CurrentVersion,
        batchId,
        "UYA",
        "NTSC-U",
        "1.00",
        fingerprint,
        new string('c', 64),
        40,
        workspace.Manifest.ProjectId.ToString(),
        workspace.CurrentFingerprint,
        41,
        0,
        [new(
            "mobys",
            "Mobys",
            ContentImportMode.AddAndReplace,
            ContentImportMode.Replace,
            ContentImportSourceState.Present,
            true,
            false,
            true,
            [],
            [],
            [],
            [$"entity:{remove}"],
            [$"entity:{protect}"],
            [],
            [],
            0,
            [],
            [])],
        [],
        []);

    private static ProjectEntity Moby(
        string name,
        ProjectAssetReference asset,
        int classId,
        int sourceIndex,
        int level,
        int uid,
        int pvarIndex,
        int group = -1) => new(
        EntityId.New(),
        name,
        "mobys",
        ProjectTransform.Identity,
        asset,
        new("UYA", level, "gameplay/core/moby_instances", sourceIndex),
        Source: new(classId, Record(classId, uid, pvarIndex, group)));

    private static OpaqueSectionCapture Capture(string name, byte[] bytes) =>
        new(name, new("gameplay/gameplay_core.bin", 0x50, 0x50, bytes.Length, 1), bytes);

    private static byte[] Record(int classId, int uid, int pvarIndex, int group)
    {
        var raw = new byte[UyaMobyInstancesReader.RecordSize];
        BinaryPrimitives.WriteInt32LittleEndian(raw, UyaMobyInstancesReader.RecordSize);
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(0x10), uid);
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(0x28), classId);
        BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(0x2c), 1);
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(0x58), group);
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(0x68), pvarIndex);
        return raw;
    }

    private static async Task<ProjectAssetReference> PutAsync(
        AssetCatalogStore catalog,
        int classId,
        byte modelByte,
        string fingerprint)
    {
        var entry = await catalog.PutAsync(
            AssetKind.Moby,
            UyaStaticLayerSchema.CanonicalFormatVersion,
            CanonicalAsset(modelByte),
            new(
                "test",
                new("UYA", "NTSC-U", "1.00", "level40", "level_wad/assets/asset_wad.bin", classId, fingerprint),
                [$"moby:{classId}"],
                ["vanilla", "game:UYA", "level:40"]));
        return new(entry.Id, AssetKind.Moby);
    }

    private static ProjectAssetReference Asset(byte value) => new(
        AssetId.Compute(AssetKind.Moby, UyaStaticLayerSchema.CanonicalFormatVersion, [value]),
        AssetKind.Moby);

    private static byte[] CanonicalAsset(byte modelByte)
    {
        const int definitionLength = 0x20;
        var bytes = new byte[19 + definitionLength];
        "HFUYA1"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(6), definitionLength);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10 + definitionLength), 1);
        bytes[14 + definitionLength] = modelByte;
        return bytes;
    }

    private static string Identity(ProjectEntity value) =>
        $"{value.EntityId}:{value.Source?.ClassId}:{value.Asset?.Id}";

    private static void Equal<T>(T expected, T actual, string context)
    {
        if (expected is Array expectedArray && actual is Array actualArray)
        {
            if (expectedArray.Cast<object?>().SequenceEqual(actualArray.Cast<object?>())) return;
        }
        else if (EqualityComparer<T>.Default.Equals(expected, actual)) return;
        throw new InvalidOperationException($"{context}: expected {expected}, got {actual}");
    }

    private static async Task ThrowsAsync<T>(Func<Task> action, string context) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"{context}: expected {typeof(T).Name}");
    }
}
