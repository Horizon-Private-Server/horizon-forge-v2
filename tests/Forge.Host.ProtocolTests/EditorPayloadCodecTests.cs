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
            [],
            [new(owner, new(ProjectReferenceDomain.Entity, "pvar.target", true,
                EntityKind: ProjectEntityKind.Moby), -1, false, "Built-in dataset tests v1")],
            [],
            [new(EditorMapGroupKind.Moby, 0, [owner], [3])],
            [],
            [new(
                "import-11111111111111111111111111111111",
                1_800_000_000_000,
                "UYA",
                "NTSC-U",
                "1.00",
                new string('a', 32),
                41,
                [new("ties", ContentImportMode.Add)],
                4,
                1,
                2,
                3)],
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
        _ = EditorPayloadCodec.EncodeCommandResult(new(
            EditorCommandResultKind.Transforms,
            null,
            [new(owner, ProjectTransform.Identity, true)],
            null,
            true,
            true,
            false,
            1));

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
        writer.WriteBoolean(false);
        var command = EditorPayloadCodec.DecodeCommand(writer.ToArray());
        Equal(EditorCommandKind.RenameGroup, command.Kind, "group command kind payload");
        Equal("Renamed group", command.Text, "group command text payload");
        Equal(groupId, command.GroupEdit!.GroupId, "group command ID payload");

        var customWriter = new PayloadWriter();
        customWriter.WriteString(Guid.NewGuid().ToString("D"));
        customWriter.WriteUInt32((uint)EditorCommandKind.ImportCustomTie);
        customWriter.WriteUInt32(0);
        customWriter.WriteBoolean(false);
        customWriter.WriteUInt32(0);
        customWriter.WriteBoolean(false);
        for (var index = 0; index < 17; index++) customWriter.WriteBoolean(false);
        customWriter.WriteBoolean(false);
        customWriter.WriteBoolean(true);
        customWriter.WriteBoolean(false);
        customWriter.WriteBoolean(true);
        customWriter.WriteString("model.gltf");
        customWriter.WriteBytes([1, 2, 3], 16 * 1024 * 1024);
        customWriter.WriteUInt32(1);
        customWriter.WriteString("buffers/model.bin");
        customWriter.WriteBytes([4, 5], 64 * 1024 * 1024);
        customWriter.WriteBoolean(true);
        var customCommand = EditorPayloadCodec.DecodeCommand(customWriter.ToArray());
        Equal(EditorCommandKind.ImportCustomTie, customCommand.Kind, "custom TIE command kind payload");
        Equal("model.gltf", customCommand.CustomModelEdit!.Import!.SourceName,
            "custom TIE source name payload");
        Equal("buffers/model.bin", customCommand.CustomModelEdit.Import.Dependencies.Single().Uri,
            "custom TIE sidecar URI payload");
        Equal(true, customCommand.CustomModelEdit.Import.ForceBackfaceCulling,
            "custom TIE backface-culling payload");

        var templateWriter = new PayloadWriter();
        templateWriter.WriteString("uya.node-cluster");
        templateWriter.WriteUInt32(1);
        templateWriter.WriteUInt32(1);
        templateWriter.WriteString("distance");
        templateWriter.WriteDouble(24);
        templateWriter.WriteBoolean(true);
        templateWriter.WriteString(groupId.ToString());
        templateWriter.WriteBoolean(true);
        templateWriter.WriteString(owner.ToString());
        var template = EditorPayloadCodec.DecodeGroupTemplateRequest(templateWriter.ToArray());
        Equal("uya.node-cluster", template.TemplateId, "group template ID payload");
        Equal(1, template.TemplateVersion, "group template version payload");
        Equal(groupId, template.GroupId, "group template target payload");
        Equal(owner, template.SeedEntityId, "group template seed payload");
        Equal(new ProjectGroupTemplateParameter("distance", 24), template.Parameters.Single(),
            "group template parameter payload");
    }

    private static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected {expected}, got {actual}");
    }
}
