using Forge.Host.Bridge;
using Forge.Host.Domain;

internal static class EditorPayloadCodecTests
{
    public static void Run()
    {
        var owner = EntityId.New();
        var snapshot = new EditorSnapshot(
            "project",
            EntityId.New(),
            "Signed reference source",
            new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
            new("UYA", "NTSC-U", "1.00", 41, "test"),
            null,
            null,
            null,
            [],
            [new(owner, new(ProjectReferenceDomain.Entity, "pvar.target", true,
                EntityKind: ProjectEntityKind.Moby), -1, false, "Built-in dataset tests v1")],
            [],
            [new(EditorMapGroupKind.Moby, 0, [owner], [3])],
            [],
            false,
            false,
            false,
            false,
            0,
            [],
            [],
            []);

        _ = EditorPayloadCodec.EncodeSnapshot(snapshot);

        var groupId = GroupId.New();
        var writer = new PayloadWriter();
        writer.WriteString(Guid.NewGuid().ToString("D"));
        writer.WriteUInt32((uint)EditorCommandKind.RenameGroup);
        writer.WriteUInt32(0);
        writer.WriteBoolean(false);
        writer.WriteUInt32(0);
        writer.WriteBoolean(true);
        writer.WriteString("Renamed group");
        for (var index = 0; index < 17; index++) writer.WriteBoolean(false);
        writer.WriteBoolean(true);
        writer.WriteBoolean(true);
        writer.WriteString(groupId.ToString());
        writer.WriteBoolean(false);
        var command = EditorPayloadCodec.DecodeCommand(writer.ToArray());
        Equal(EditorCommandKind.RenameGroup, command.Kind, "group command kind payload");
        Equal("Renamed group", command.Text, "group command text payload");
        Equal(groupId, command.GroupEdit!.GroupId, "group command ID payload");
    }

    private static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected {expected}, got {actual}");
    }
}
