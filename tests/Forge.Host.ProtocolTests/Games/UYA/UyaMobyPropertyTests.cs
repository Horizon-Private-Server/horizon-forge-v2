using System.Buffers.Binary;
using Forge.Host.Domain;
using Forge.Host.Games.UYA;
using Forge.Host.Bridge;
using RatchetPs2.Games.UYA.Gameplay;

namespace Forge.Host.ProtocolTests.Games.UYA;

internal static class UyaMobyPropertyTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-moby-properties-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var projectPath = Path.Combine(root, "project");
            var entity = Moby();
            var created = await ForgeProjectWorkspace.CreateAsync(
                projectPath,
                "Moby properties",
                new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
                new("UYA", "NTSC-U", "1.00", 3, new string('a', 32)),
                [entity]);
            var sourceFingerprint = created.CurrentFingerprint;
            await using var runtime = new EditorRuntime(
                mobyPropertyResolver: UyaMobyPropertyService.Describe,
                mobyPropertyCommandExecutor: UyaMobyPropertyService.ExecuteAsync);
            var snapshot = await runtime.OpenAsync(projectPath, TimeSpan.FromMilliseconds(10));
            var properties = snapshot.Entities.Single().MobyProperties
                ?? throw new InvalidOperationException("Moby property descriptors are missing.");
            Equal(14, properties.Count, "moby descriptor count");
            Equal(false, properties.Single(value => value.Key == UyaMobyPropertyService.ClassId).Editable,
                "OClass is read-only");
            Equal("units", properties.Single(value => value.Key == UyaMobyPropertyService.DrawDistance).Unit,
                "distance descriptor unit");
            Equal(true, properties.Single(value => value.Key == UyaMobyPropertyService.RootedDistance)
                .Help?.Contains("-1", StringComparison.Ordinal) == true,
                "sentinel descriptor help");
            Equal(true, runtime.HasCapability("editor.moby-property.update"), "moby property capability");
            Equal(true, EditorPayloadCodec.EncodeSnapshot(snapshot).Length > 0, "moby descriptor bridge payload");

            snapshot = await runtime.ExecuteAsync(Command(
                entity.EntityId,
                UyaMobyPropertyService.Bolts,
                new(EditorMobyPropertyValueKind.Integer, Integer: 42)));
            Equal(true, snapshot.IsDirty && snapshot.CanUndo, "moby edit is dirty and undoable");
            Equal(true, snapshot.Entities.Single().State.Dirty, "moby edit marks its entity dirty");
            Equal(42, snapshot.Entities.Single().MobyProperties!
                .Single(value => value.Key == UyaMobyPropertyService.Bolts).Value.Integer,
                "edited moby property is reprojected");
            await WaitForRecoveryAsync(runtime, snapshot.LastEventSequence);

            snapshot = await runtime.ExecuteAsync(new(
                Guid.NewGuid().ToString("D"), EditorCommandKind.Undo, []));
            Equal(0, snapshot.Entities.Single().MobyProperties!
                .Single(value => value.Key == UyaMobyPropertyService.Bolts).Value.Integer,
                "undo restores moby property bytes");
            snapshot = await runtime.ExecuteAsync(new(
                Guid.NewGuid().ToString("D"), EditorCommandKind.Redo, []));
            Equal(42, snapshot.Entities.Single().MobyProperties!
                .Single(value => value.Key == UyaMobyPropertyService.Bolts).Value.Integer,
                "redo restores moby property bytes");

            await ThrowsAsync<ArgumentException>(() => runtime.ExecuteAsync(Command(
                entity.EntityId, "instance.unknown",
                new(EditorMobyPropertyValueKind.Integer, Integer: 1))));
            await ThrowsAsync<ArgumentException>(() => runtime.ExecuteAsync(Command(
                entity.EntityId, UyaMobyPropertyService.ClassId,
                new(EditorMobyPropertyValueKind.Integer, Integer: 0x401))));
            await ThrowsAsync<ArgumentOutOfRangeException>(() => runtime.ExecuteAsync(Command(
                entity.EntityId, UyaMobyPropertyService.Bolts,
                new(EditorMobyPropertyValueKind.Boolean, Boolean: true))));
            await ThrowsAsync<ArgumentOutOfRangeException>(() => runtime.ExecuteAsync(Command(
                entity.EntityId, UyaMobyPropertyService.RootedDistance,
                new(EditorMobyPropertyValueKind.Float, Float: float.PositiveInfinity))));

            snapshot = await runtime.ExecuteAsync(new(
                Guid.NewGuid().ToString("D"), EditorCommandKind.SetEntityState, [entity.EntityId],
                State: new(Locked: true)));
            await ThrowsAsync<ArgumentException>(() => runtime.ExecuteAsync(Command(
                entity.EntityId, UyaMobyPropertyService.Bolts,
                new(EditorMobyPropertyValueKind.Integer, Integer: 7))));
            snapshot = await runtime.ExecuteAsync(new(
                Guid.NewGuid().ToString("D"), EditorCommandKind.SetEntityState, [entity.EntityId],
                State: new(Locked: false)));
            snapshot = await runtime.SaveAsync();
            Equal(false, snapshot.IsDirty, "saving clears moby property dirtiness");

            var saved = await ForgeProjectWorkspace.OpenAsync(projectPath);
            Equal(false, sourceFingerprint == saved.CurrentFingerprint,
                "moby native bytes participate in project and bake input fingerprinting");
            Equal(42, UyaMobyInstancesReader.ReadInstance(saved.Content.Entities.Single().Source!.RawRecord).Bolts,
                "saved moby property persists in the native record");

            var multiPath = Path.Combine(root, "multi");
            var multiMobys = new[] { Moby(0), Moby(1) };
            await ForgeProjectWorkspace.CreateAsync(
                multiPath,
                "Multi moby properties",
                new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
                new("UYA", "NTSC-U", "1.00", 3, new string('c', 32)),
                multiMobys);
            await runtime.OpenAsync(multiPath, TimeSpan.Zero);
            snapshot = await runtime.ExecuteAsync(Command(
                multiMobys.Select(value => value.EntityId).ToArray(), UyaMobyPropertyService.Bolts,
                new(EditorMobyPropertyValueKind.Integer, Integer: 7)));
            Equal(true, snapshot.Entities.All(value => value.MobyProperties!
                .Single(property => property.Key == UyaMobyPropertyService.Bolts).Value.Integer == 7),
                "multi-edit updates every compatible moby");
            snapshot = await runtime.ExecuteAsync(new(
                Guid.NewGuid().ToString("D"), EditorCommandKind.Undo, []));
            Equal(true, snapshot.Entities.All(value => value.MobyProperties!
                .Single(property => property.Key == UyaMobyPropertyService.Bolts).Value.Integer == 0),
                "one undo restores the whole multi-edit");

            var atomicPath = Path.Combine(root, "atomic");
            var atomicMobys = new[] { Moby(0), Moby(1) };
            BinaryPrimitives.WriteInt32LittleEndian(atomicMobys[1].Source!.RawRecord.AsSpan(0x28), 0x401);
            await ForgeProjectWorkspace.CreateAsync(
                atomicPath,
                "Atomic moby properties",
                new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
                new("UYA", "NTSC-U", "1.00", 3, new string('d', 32)),
                atomicMobys);
            await runtime.OpenAsync(atomicPath, TimeSpan.Zero);
            await ThrowsAsync<InvalidDataException>(() => runtime.ExecuteAsync(Command(
                atomicMobys.Select(value => value.EntityId).ToArray(), UyaMobyPropertyService.Bolts,
                new(EditorMobyPropertyValueKind.Integer, Integer: 9))));
            snapshot = await runtime.GetSnapshotAsync();
            Equal(0, snapshot.Entities[0].MobyProperties!
                .Single(value => value.Key == UyaMobyPropertyService.Bolts).Value.Integer,
                "failed multi-edit leaves the first moby unchanged");

            var inconsistentPath = Path.Combine(root, "inconsistent");
            var inconsistent = Moby();
            BinaryPrimitives.WriteInt32LittleEndian(inconsistent.Source!.RawRecord.AsSpan(0x28), 0x401);
            await ForgeProjectWorkspace.CreateAsync(
                inconsistentPath,
                "Inconsistent moby",
                new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
                new("UYA", "NTSC-U", "1.00", 3, new string('b', 32)),
                [inconsistent]);
            await runtime.OpenAsync(inconsistentPath, TimeSpan.Zero);
            await ThrowsAsync<InvalidDataException>(() => runtime.ExecuteAsync(Command(
                inconsistent.EntityId, UyaMobyPropertyService.Bolts,
                new(EditorMobyPropertyValueKind.Integer, Integer: 1))));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ProjectEntity Moby(int sourceIndex = 0)
    {
        var raw = new byte[UyaMobyInstancesReader.RecordSize];
        BinaryPrimitives.WriteInt32LittleEndian(raw, UyaMobyInstancesReader.RecordSize);
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(0x28), 0x400);
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(0x10), sourceIndex);
        BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(0x2c), 1);
        BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(0x60), -1);
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(0x68), -1);
        return new(
            EntityId.New(),
            $"Test moby {sourceIndex}",
            "mobys",
            ProjectTransform.Identity,
            null,
            new("UYA", 3, "gameplay/core/moby_instances", sourceIndex),
            Source: new(0x400, raw, SourceIndex: sourceIndex));
    }

    private static EditorCommand Command(
        EntityId entityId,
        string fieldKey,
        EditorMobyPropertyValue value) => Command([entityId], fieldKey, value);

    private static EditorCommand Command(
        IReadOnlyList<EntityId> entityIds,
        string fieldKey,
        EditorMobyPropertyValue value) => new(
            Guid.NewGuid().ToString("D"),
            EditorCommandKind.SetMobyInstanceProperty,
            entityIds,
            MobyPropertyEdit: new(fieldKey, 0x400, value));

    private static async Task WaitForRecoveryAsync(EditorRuntime runtime, long afterSequence)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if ((await runtime.ReadEventsAsync(afterSequence, 100))
                .Any(value => value.Kind == EditorEventKind.RecoveryWritten)) return;
            await Task.Delay(10);
        }
        throw new InvalidOperationException("Moby property edit did not write recovery state.");
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
