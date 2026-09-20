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
            var baseLevel = new ProjectBaseLevel("UYA", "NTSC-U", "1.00", 3, UyaIsoService.SupportedMd5);
            var firstPath = Path.Combine(root, "first");
            var secondPath = Path.Combine(root, "second");
            await ForgeProjectWorkspace.CreateAsync(firstPath, "First", target, baseLevel,
                [Entity(firstId, "One"), Entity(secondId, "Two")]);
            await ForgeProjectWorkspace.CreateAsync(secondPath, "Second", target, baseLevel,
                [Entity(EntityId.New(), "Other")]);

            var snapshot = await runtime.OpenAsync(firstPath, TimeSpan.FromMilliseconds(25));
            Equal(false, snapshot.IsDirty, "opened runtime clean state");
            Equal(true, runtime.HasCapability("editor.transform.update"), "runtime capability");
            Equal("select", snapshot.Tools.Single().Id, "registered select tool");

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
            await WaitForAsync(async () => (await runtime.ReadEventsAsync(0, EditorRuntimeEventLimit))
                .Any(value => value.Kind == EditorEventKind.RecoveryWritten));
            var firstWorkspace = await ForgeProjectWorkspace.OpenAsync(firstPath);
            Equal(true, (await firstWorkspace.ListRecoveriesAsync()).Count > 0, "idle autosave writes recovery");

            snapshot = await runtime.SaveAsync();
            Equal(false, snapshot.IsDirty, "manual save marks runtime clean");
            Equal(true, snapshot.Entities.All(entity => !entity.State.Dirty), "save clears entity dirty state");
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
