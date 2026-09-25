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

    private const int EditorRuntimeEventLimit = 1_024;

    private static ProjectEntity Entity(EntityId id, string name) => new(
        id, name, "mobys", ProjectTransform.Identity, null, new("UYA", 3, "gameplay/core/moby_instances", 0));

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
