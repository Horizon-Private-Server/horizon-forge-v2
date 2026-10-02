using Forge.Host.Games.UYA;
using System.Text.Json;
using Forge.Host.Domain;

internal static class EditorRuntimeTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-editor-runtime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await using var runtime = new EditorRuntime();
        try
        {
            var firstId = EntityId.New();
            var secondId = EntityId.New();
            var target = new ProjectTargetProfile("UYA", "NTSC-U", "1.00", "uya-ntsc-u");
            var baseLevel = new ProjectBaseLevel(
                "UYA", "NTSC-U", "1.00", 3, UyaIsoService.SupportedMd5,
                EntityVersion: ProjectSchema.CurrentBaseEntityVersion);
            var firstPath = Path.Combine(root, "first");
            var secondPath = Path.Combine(root, "second");
            await ForgeProjectWorkspace.CreateAsync(firstPath, "First", target, baseLevel,
                [Entity(firstId, "One"), Entity(secondId, "Two")]);
            await ForgeProjectWorkspace.CreateAsync(secondPath, "Second", target, baseLevel,
                [Entity(EntityId.New(), "Other")]);
            await VerifyAssetPlacementHistoryAsync(root, target, baseLevel);
            await VerifySkyShellHistoryAsync(root, target, baseLevel);
            await VerifyUyaSkyShellCommandsAsync(root, target, baseLevel);
            await VerifyCollisionHistoryAsync(root, target, baseLevel);

            var snapshot = await runtime.OpenAsync(firstPath, TimeSpan.FromMilliseconds(25));
            Equal(false, snapshot.IsDirty, "opened runtime clean state");
            Equal(true, runtime.HasCapability("editor.transform.update"), "runtime capability");
            Equal(true, snapshot.Tools.Select(tool => tool.Id).SequenceEqual(["select", "translate", "rotate", "scale"]),
                "registered transform tools");

            var selection = Command(EditorCommandKind.SetSelection, [firstId, secondId]);
            var serialized = JsonSerializer.Serialize(selection);
            var roundTrip = JsonSerializer.Deserialize<EditorCommand>(serialized)
                ?? throw new InvalidOperationException("Editor command did not deserialize");
            Equal(selection.Id, roundTrip.Id, "serializable command ID");
            Equal(true, selection.EntityIds.SequenceEqual(roundTrip.EntityIds), "serializable command entities");
            snapshot = await runtime.ExecuteAsync(roundTrip);
            Equal(true, snapshot.Selection.SequenceEqual([firstId, secondId]), "shared Entity-ID selection");

            var sequenceBeforeFailure = snapshot.LastEventSequence;
            await ThrowsAsync<ArgumentException>(() => runtime.ExecuteAsync(
                Command(EditorCommandKind.SetSelection, [EntityId.New()])));
            snapshot = await runtime.GetSnapshotAsync();
            Equal(sequenceBeforeFailure, snapshot.LastEventSequence, "invalid command emits no event");
            Equal(true, snapshot.Selection.SequenceEqual([firstId, secondId]), "invalid command changes no selection");

            var transform = ProjectTransform.Identity with { Position = new(10, 20, 30) };
            snapshot = await runtime.ExecuteAsync(Command(EditorCommandKind.UpdateTransform, [firstId], transform));
            Equal(true, snapshot.IsDirty, "mutation marks runtime dirty");
            Equal(new ProjectVector3(10, 20, 30),
                snapshot.Entities.Single(entity => entity.EntityId == firstId).Transform.Position,
                "runtime transform mutation");
            Equal(true, snapshot.CanUndo, "mutation enables undo");
            snapshot = await runtime.ExecuteAsync(Command(EditorCommandKind.Undo, []));
            Equal(ProjectTransform.Identity.Position,
                snapshot.Entities.Single(entity => entity.EntityId == firstId).Transform.Position,
                "undo restores transform");
            Equal(false, snapshot.IsDirty, "undo restores clean state");
            Equal(true, snapshot.CanRedo, "undo enables redo");
            snapshot = await runtime.ExecuteAsync(Command(EditorCommandKind.Redo, []));
            Equal(transform.Position,
                snapshot.Entities.Single(entity => entity.EntityId == firstId).Transform.Position,
                "redo restores transform mutation");
            Equal(false, snapshot.CanRedo, "redo consumes redo entry");
            var levelSettings = new ProjectLevelSettings(new(1, 2, 3), new(4, 5, 6), 10, 175, 255, 0);
            snapshot = await runtime.ExecuteAsync(new(
                Guid.NewGuid().ToString("D"), EditorCommandKind.UpdateLevelSettings, [], LevelSettings: levelSettings));
            Equal(levelSettings, snapshot.LevelSettings, "runtime level settings mutation");
            snapshot = await runtime.ExecuteAsync(Command(EditorCommandKind.Undo, []));
            Equal(null, snapshot.LevelSettings, "undo restores level settings");
            snapshot = await runtime.ExecuteAsync(Command(EditorCommandKind.Redo, []));
            Equal(levelSettings, snapshot.LevelSettings, "redo restores level settings mutation");
            var secondTransform = ProjectTransform.Identity with { Position = new(40, 50, 60) };
            snapshot = await runtime.ExecuteAsync(new(
                Guid.NewGuid().ToString("D"), EditorCommandKind.UpdateTransforms, [firstId, secondId],
                Transforms: [new(firstId, transform), new(secondId, secondTransform)]));
            Equal(secondTransform.Position,
                snapshot.Entities.Single(entity => entity.EntityId == secondId).Transform.Position,
                "runtime batch transform mutation");
            snapshot = await runtime.ExecuteAsync(Command(EditorCommandKind.CopyEntities, [firstId]));
            Equal(true, snapshot.CanPaste, "copy enables same-project paste");
            snapshot = await runtime.ExecuteAsync(Command(EditorCommandKind.PasteEntities, []));
            var pastedId = snapshot.Selection.Single();
            Equal(3, snapshot.Entities.Count, "paste adds copied entity");
            Equal(null, snapshot.Entities.Single(entity => entity.EntityId == pastedId).Provenance,
                "paste clears source provenance");
            snapshot = await runtime.ExecuteAsync(Command(EditorCommandKind.Undo, []));
            Equal(2, snapshot.Entities.Count, "paste is undoable");
            Equal(true, snapshot.Selection.SequenceEqual([firstId, secondId]), "undo restores pre-paste selection");
            snapshot = await runtime.ExecuteAsync(Command(EditorCommandKind.DuplicateEntities, [secondId]));
            var duplicateId = snapshot.Selection.Single();
            Equal(true, duplicateId != secondId, "duplicate receives a new entity ID");
            snapshot = await runtime.ExecuteAsync(Command(EditorCommandKind.DeleteEntities, [duplicateId]));
            Equal(2, snapshot.Entities.Count, "delete removes selected entity");
            Equal(true, snapshot.Selection.SequenceEqual([secondId]), "delete chooses a stable selection fallback");
            snapshot = await runtime.ExecuteAsync(Command(EditorCommandKind.Undo, []));
            Equal(true, snapshot.Entities.Any(entity => entity.EntityId == duplicateId), "delete is undoable");
            snapshot = await runtime.ExecuteAsync(Command(EditorCommandKind.Undo, []));
            Equal(2, snapshot.Entities.Count, "duplicate is undoable");
            Equal(true, snapshot.Entities.Single(entity => entity.EntityId == firstId).State.Dirty,
                "mutated entity dirty state");
            snapshot = await runtime.ExecuteAsync(new(
                Guid.NewGuid().ToString("D"), EditorCommandKind.RenameEntity, [firstId], Text: "Renamed entity"));
            snapshot = await runtime.ExecuteAsync(new(
                Guid.NewGuid().ToString("D"), EditorCommandKind.SetEntityLayer, [firstId, secondId], Text: "gameplay"));
            snapshot = await runtime.ExecuteAsync(new(
                Guid.NewGuid().ToString("D"), EditorCommandKind.SetEntityState, [firstId, secondId],
                State: new(Hidden: true, Locked: true)));
            Equal("Renamed entity", snapshot.Entities.Single(entity => entity.EntityId == firstId).Name,
                "entity rename command");
            Equal(true, snapshot.Entities.All(entity => entity.Layer == "gameplay"), "multi-entity layer command");
            Equal(true, snapshot.Entities.All(entity => entity.State is { Hidden: true, Locked: true }),
                "multi-entity state command");
            await ThrowsAsync<ArgumentException>(() => runtime.ExecuteAsync(new(
                Guid.NewGuid().ToString("D"), EditorCommandKind.RenameEntity, [firstId], Text: "Locked rename")));
            await ThrowsAsync<ArgumentException>(() => runtime.ExecuteAsync(
                Command(EditorCommandKind.UpdateTransform, [firstId], ProjectTransform.Identity)));
            await ThrowsAsync<ArgumentException>(() => runtime.ExecuteAsync(
                Command(EditorCommandKind.DeleteEntities, [firstId])));
            snapshot = await runtime.ExecuteAsync(new(
                Guid.NewGuid().ToString("D"), EditorCommandKind.SetEntityState, [firstId],
                State: new(Locked: false)));
            Equal(false, snapshot.Entities.Single(entity => entity.EntityId == firstId).State.Locked,
                "locked entity can be unlocked");
            await WaitForAsync(async () => (await runtime.ReadEventsAsync(0, EditorRuntimeEventLimit))
                .Any(value => value.Kind == EditorEventKind.RecoveryWritten));
            var firstWorkspace = await ForgeProjectWorkspace.OpenAsync(firstPath);
            Equal(true, (await firstWorkspace.ListRecoveriesAsync()).Count > 0, "idle autosave writes recovery");

            snapshot = await runtime.SaveAsync();
            Equal(false, snapshot.IsDirty, "manual save marks runtime clean");
            Equal(true, snapshot.Entities.All(entity => !entity.State.Dirty), "save clears entity dirty state");
            Equal(true, snapshot.CanUndo, "save preserves session history");
            for (var index = 0; index < 101; index++)
                snapshot = await runtime.ExecuteAsync(new(
                    Guid.NewGuid().ToString("D"), EditorCommandKind.RenameProject, [], Text: $"History {index}"));
            var undoCount = 0;
            while (snapshot.CanUndo)
            {
                snapshot = await runtime.ExecuteAsync(Command(EditorCommandKind.Undo, []));
                undoCount++;
            }
            Equal(100, undoCount, "history entry cap");
            Equal("History 0", snapshot.ProjectName, "history evicts the oldest complete command");
            snapshot = await runtime.ExecuteAsync(new(
                Guid.NewGuid().ToString("D"), EditorCommandKind.RenameProject, [], Text: "Renamed"));
            Equal("Renamed", snapshot.ProjectName, "rename command");
            await ThrowsAsync<IOException>(() => runtime.OpenAsync(
                Path.Combine(root, "missing"), TimeSpan.Zero));
            Equal("Renamed", (await runtime.GetSnapshotAsync()).ProjectName, "failed transition preserves active project");
            await runtime.OpenAsync(secondPath, TimeSpan.Zero);
            var reopenedFirst = await ForgeProjectWorkspace.OpenAsync(firstPath);
            Equal(true, (await reopenedFirst.ListRecoveriesAsync()).Any(recovery => recovery.Name == "Renamed"),
                "project transition flushes dirty recovery");

            var events = await runtime.ReadEventsAsync(0, EditorRuntimeEventLimit);
            Equal(true, events.Zip(events.Skip(1)).All(pair => pair.First.Sequence < pair.Second.Sequence),
                "runtime events are ordered");
            _ = JsonSerializer.Serialize(events);
            await runtime.CloseAsync();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task VerifyCollisionHistoryAsync(
        string root,
        ProjectTargetProfile target,
        ProjectBaseLevel baseLevel)
    {
        var projectPath = Path.Combine(root, "collision");
        var id = EntityId.New();
        var entity = new ProjectEntity(
            id,
            "Solid #0",
            "collision",
            ProjectTransform.Identity,
            new(AssetId.Parse(new string('c', AssetId.TextLength)), AssetKind.Collision),
            new("UYA", baseLevel.Level, "collision/primary", 0),
            Collision: new(ProjectCollisionPieceKind.Solid, 0, 0, 1, 3, [new(0x21, 1)]));
        await ForgeProjectWorkspace.CreateAsync(projectPath, "Collision", target, baseLevel, [entity]);
        await using var runtime = new EditorRuntime(
            transformCapabilityResolver: UyaEditorCapabilities.ResolveTransformCapabilities);
        var snapshot = await runtime.OpenAsync(projectPath, TimeSpan.Zero);
        Equal(EditorTransformCapabilities.Translate, snapshot.Entities.Single().TransformCapabilities,
            "collision exposes translation only");
        Equal(ProjectCollisionPieceKind.Solid, snapshot.Entities.Single().Collision!.Kind,
            "collision metadata reaches editor snapshot");

        var requested = ProjectTransform.Identity with { Position = new(0.08f, 0.02f, -0.08f) };
        snapshot = await runtime.ExecuteAsync(Command(EditorCommandKind.UpdateTransform, [id], requested));
        Equal(new ProjectVector3(0.0625f, 0, -0.078125f), snapshot.Entities.Single().Transform.Position,
            "solid collision translation is quantized before commit");
        await ThrowsAsync<ArgumentException>(() => runtime.ExecuteAsync(Command(
            EditorCommandKind.UpdateTransform,
            [id],
            requested with { Rotation = new(0, 0, 1, 0) })));
        await ThrowsAsync<ArgumentException>(() => runtime.ExecuteAsync(Command(EditorCommandKind.CopyEntities, [id])));
        await ThrowsAsync<ArgumentException>(() => runtime.ExecuteAsync(new(
            Guid.NewGuid().ToString("D"), EditorCommandKind.SetEntityLayer, [id], Text: "world")));
        snapshot = await runtime.ExecuteAsync(new(
            Guid.NewGuid().ToString("D"), EditorCommandKind.SetEntityState, [id], State: new(Disabled: true)));
        Equal(true, snapshot.Entities.Single().State.Disabled, "collision can be disabled");
        snapshot = await runtime.ExecuteAsync(Command(EditorCommandKind.DeleteEntities, [id]));
        Equal(0, snapshot.Entities.Count, "collision can be deleted");
        snapshot = await runtime.ExecuteAsync(Command(EditorCommandKind.Undo, []));
        Equal(id, snapshot.Entities.Single().EntityId, "collision delete is undoable");
    }

    private static async Task VerifyAssetPlacementHistoryAsync(
        string root,
        ProjectTargetProfile target,
        ProjectBaseLevel baseLevel)
    {
        var projectPath = Path.Combine(root, "placement");
        await ForgeProjectWorkspace.CreateAsync(projectPath, "Placement", target, baseLevel, []);
        var placedId = EntityId.New();
        await using var runtime = new EditorRuntime((_, _, placement, _) => Task.FromResult(new ProjectEntity(
            placedId, "Placed", "mobys", placement.Transform,
            new(placement.AssetId, placement.Kind), Source: new(placement.ClassId, new byte[0x88]))));
        var catalogPath = Path.Combine(root, "placement-catalog");
        await runtime.OpenAsync(projectPath, catalogPath, TimeSpan.Zero);
        var placement = new EditorAssetPlacement(
            AssetId.Parse(new string('a', AssetId.TextLength)), AssetKind.Moby, 0x947,
            ProjectTransform.Identity with { Position = new(1, 2, 3) });
        var snapshot = await runtime.ExecuteAsync(new(
            Guid.NewGuid().ToString("D"), EditorCommandKind.CreateEntityFromAsset, [], Placement: placement));
        Equal(true, snapshot.Selection.SequenceEqual([placedId]), "placement selects created entity");
        Equal(placedId, snapshot.Entities.Single().EntityId, "placement keeps generated entity ID");
        snapshot = await runtime.ExecuteAsync(Command(EditorCommandKind.Undo, []));
        Equal(0, snapshot.Entities.Count, "placement is undoable");
        snapshot = await runtime.ExecuteAsync(Command(EditorCommandKind.Redo, []));
        Equal(placedId, snapshot.Entities.Single().EntityId, "placement redo preserves generated entity ID");
    }

    private static async Task VerifySkyShellHistoryAsync(
        string root,
        ProjectTargetProfile target,
        ProjectBaseLevel baseLevel)
    {
        var assetId = AssetId.Parse(new string('b', AssetId.TextLength));
        var firstId = EntityId.New();
        var secondId = EntityId.New();
        var projectPath = Path.Combine(root, "sky-shells");
        await ForgeProjectWorkspace.CreateAsync(projectPath, "Sky shells", target, baseLevel,
        [
            SkyShell(firstId, assetId, 0),
            SkyShell(secondId, assetId, 1),
        ]);
        await using var runtime = new EditorRuntime(skyShellCommandExecutor: (workspace, _, command, _) =>
        {
            if (command.Kind == EditorCommandKind.UpdateSkyShell)
            {
                var entity = workspace.Content.Entities.Single(value => value.EntityId == command.EntityIds[0]);
                workspace.UpdateSkyShell(entity.EntityId, entity.SkyShell! with
                {
                    InitialRotationRadians = command.SkyShellUpdate!.InitialRotationRadians
                        ?? entity.SkyShell.InitialRotationRadians,
                    AngularVelocityRadiansPerSecond = command.SkyShellUpdate.AngularVelocityRadiansPerSecond
                        ?? entity.SkyShell.AngularVelocityRadiansPerSecond,
                });
            }
            else if (command.Kind == EditorCommandKind.ReorderSkyShell)
            {
                workspace.ReorderSkyShell(command.EntityIds[0], command.DestinationOrder!.Value);
            }
            else if (command.Kind is not (EditorCommandKind.DeleteEntities
                or EditorCommandKind.DuplicateEntities or EditorCommandKind.PasteEntities
                or EditorCommandKind.SetEntityState))
            {
                throw new InvalidOperationException("Unexpected fake sky shell command.");
            }
            return Task.FromResult(command.EntityIds);
        });
        var snapshot = await runtime.OpenAsync(projectPath, Path.Combine(root, "sky-catalog"), TimeSpan.Zero);
        var first = snapshot.Entities.Single(entity => entity.EntityId == firstId);
        Equal(0, first.SkyShell!.Order, "sky shell snapshot order");
        Equal(EditorTransformCapabilities.None, first.TransformCapabilities, "sky shell transform tools disabled");
        Equal(false, first.State.ReadOnly, "sky shell structural commands remain enabled");

        var rotation = new ProjectVector3(0.25f, -0.5f, 0.75f);
        snapshot = await runtime.ExecuteAsync(new(
            Guid.NewGuid().ToString("D"), EditorCommandKind.UpdateSkyShell, [firstId],
            SkyShellUpdate: new(InitialRotationRadians: rotation)));
        Equal(rotation, snapshot.Entities.Single(entity => entity.EntityId == firstId)
            .SkyShell!.InitialRotationRadians, "sky shell rotation update");
        snapshot = await runtime.ExecuteAsync(Command(EditorCommandKind.Undo, []));
        Equal(new ProjectVector3(0, 0, 0), snapshot.Entities.Single(entity => entity.EntityId == firstId)
            .SkyShell!.InitialRotationRadians, "sky shell update undo");

        snapshot = await runtime.ExecuteAsync(new(
            Guid.NewGuid().ToString("D"), EditorCommandKind.ReorderSkyShell, [secondId], DestinationOrder: 0));
        Equal(0, snapshot.Entities.Single(entity => entity.EntityId == secondId).SkyShell!.Order,
            "sky shell reorder command");
        Equal(1, snapshot.Entities.Single(entity => entity.EntityId == firstId).SkyShell!.Order,
            "sky shell reorder normalizes sibling order");
        snapshot = await runtime.ExecuteAsync(Command(EditorCommandKind.DeleteEntities, [secondId]));
        Equal(0, snapshot.Entities.Single(entity => entity.EntityId == firstId).SkyShell!.Order,
            "sky shell deletion normalizes remaining order");
    }

    private static async Task VerifyUyaSkyShellCommandsAsync(
        string root,
        ProjectTargetProfile target,
        ProjectBaseLevel baseLevel)
    {
        var projectPath = Path.Combine(root, "uya-sky-shells");
        var catalogPath = Path.Combine(root, "uya-sky-catalog");
        var catalog = await AssetCatalogStore.OpenAsync(catalogPath);
        var bytes = BuildSkybox();
        var source = new OpaqueContentSource(
            "UYA", "NTSC-U", target.Revision, baseLevel.Level, baseLevel.SourceFingerprint);
        var payload = new UyaBaseLayerPayload(
            BakeLayerId.Sky, "sky.bin", AssetKind.Sky, "level_wad/assets/asset_wad.bin", 0, bytes);
        var entry = (await catalog.PutManyAsync(
            UyaBaseLayerService.CreateCatalogPuts([payload], source, "test-sky"))).Single();
        var firstId = EntityId.New();
        await ForgeProjectWorkspace.CreateAsync(projectPath, "UYA sky commands", target, baseLevel,
            [SkyShell(firstId, entry.Id, 0)]);
        await UyaBaseLayerStore.WriteAsync(projectPath, source, [payload], [entry]);

        await using var runtime = new EditorRuntime(
            skyShellCommandExecutor: UyaSkyShellEditorService.ExecuteAsync);
        var snapshot = await runtime.OpenAsync(projectPath, catalogPath, TimeSpan.Zero);
        snapshot = await runtime.ExecuteAsync(new(
            Guid.NewGuid().ToString("D"),
            EditorCommandKind.AddSkyShellFromAsset,
            [],
            SkyShellSource: new(entry.Id, 0)));
        var addedId = snapshot.Selection.Single();
        Equal(2, snapshot.Entities.Count(entity => entity.SkyShell is not null),
            "UYA sky shell add command");

        var requested = new ProjectVector3(0.1f, -0.2f, 0.3f);
        snapshot = await runtime.ExecuteAsync(new(
            Guid.NewGuid().ToString("D"),
            EditorCommandKind.UpdateSkyShell,
            [addedId],
            SkyShellUpdate: new(InitialRotationRadians: requested)));
        const float tick = MathF.PI / 32768f;
        var expected = new ProjectVector3(
            MathF.Round(requested.X / tick, MidpointRounding.AwayFromZero) * tick,
            MathF.Round(requested.Y / tick, MidpointRounding.AwayFromZero) * tick,
            MathF.Round(requested.Z / tick, MidpointRounding.AwayFromZero) * tick);
        Equal(expected, snapshot.Entities.Single(entity => entity.EntityId == addedId)
            .SkyShell!.InitialRotationRadians, "UYA sky shell native rotation quantization");

        snapshot = await runtime.ExecuteAsync(new(
            Guid.NewGuid().ToString("D"),
            EditorCommandKind.ReorderSkyShell,
            [addedId],
            DestinationOrder: 0));
        Equal(0, snapshot.Entities.Single(entity => entity.EntityId == addedId).SkyShell!.Order,
            "UYA sky shell validated reorder");

        while (snapshot.Entities.Count(entity => entity.SkyShell is not null) < 8)
            snapshot = await runtime.ExecuteAsync(new(
                Guid.NewGuid().ToString("D"),
                EditorCommandKind.AddSkyShellFromAsset,
                [],
                SkyShellSource: new(entry.Id, 0)));
        var sequence = snapshot.LastEventSequence;
        await ThrowsAsync<InvalidDataException>(() => runtime.ExecuteAsync(new(
            Guid.NewGuid().ToString("D"),
            EditorCommandKind.AddSkyShellFromAsset,
            [],
            SkyShellSource: new(entry.Id, 0))));
        snapshot = await runtime.GetSnapshotAsync();
        Equal(8, snapshot.Entities.Count(entity => entity.SkyShell is not null),
            "UYA sky shell limit failure preserves composition");
        Equal(sequence, snapshot.LastEventSequence,
            "UYA sky shell limit failure emits no project event");

        await runtime.SaveAsync();
        await runtime.CloseAsync();
        var reopened = await ForgeProjectWorkspace.OpenAsync(projectPath);
        Equal(true, reopened.Content.Entities.Where(entity => entity.SkyShell is not null)
            .OrderBy(entity => entity.SkyShell!.Order)
            .Select(entity => entity.SkyShell!.Order)
            .SequenceEqual(Enumerable.Range(0, 8)),
            "UYA sky shell order survives save and reopen");
    }

    internal static byte[] BuildSkybox()
    {
        var bytes = new byte[0x50];
        using var writer = new BinaryWriter(new MemoryStream(bytes, writable: true));
        writer.BaseStream.Position = 6;
        writer.Write((short)1);
        writer.BaseStream.Position = 16;
        writer.Write((uint)0x40);
        writer.Write((uint)0x40);
        writer.Write(0x40);
        writer.Write((uint)0);
        writer.Write((uint)0x40);
        writer.BaseStream.Position = 0x40;
        writer.Write((short)0);
        writer.Write((short)1);
        writer.Write((short)2);
        writer.Write((short)-3);
        writer.Write((short)4);
        writer.Write((short)1);
        writer.Write((short)0);
        writer.Write((short)-1);
        return bytes;
    }

    private const int EditorRuntimeEventLimit = 1_024;

    private static ProjectEntity Entity(EntityId id, string name) => new(
        id, name, "mobys", ProjectTransform.Identity, null, new("UYA", 3, "gameplay/core/moby_instances", 0));

    private static ProjectEntity SkyShell(EntityId id, AssetId assetId, int order) => new(
        id,
        $"Sky shell {order + 1}",
        "sky",
        ProjectTransform.Identity,
        new(assetId, AssetKind.Sky),
        new("UYA", 3, "level_wad/assets/sky", order),
        SkyShell: new(order, order, new(0, 0, 0), new(0, 0, 0)));

    private static EditorCommand Command(
        EditorCommandKind kind,
        IReadOnlyList<EntityId> ids,
        ProjectTransform? transform = null) => new(Guid.NewGuid().ToString("D"), kind, ids, transform);

    private static async Task WaitForAsync(Func<Task<bool>> predicate)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (await predicate()) return;
            await Task.Delay(10);
        }
        throw new InvalidOperationException("Timed out waiting for editor runtime state.");
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }

    private static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected {expected}, got {actual}");
    }
}
