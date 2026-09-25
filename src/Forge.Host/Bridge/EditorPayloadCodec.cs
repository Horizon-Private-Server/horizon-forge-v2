using Forge.Host.Domain;

namespace Forge.Host.Bridge;

internal readonly record struct EditorOpenRequest(string ProjectPath, string CatalogRootPath, uint AutosaveSeconds);
internal readonly record struct EditorEventRequest(ulong AfterSequence, uint Limit);

internal static class EditorPayloadCodec
{
    private const uint MaxEntities = 100_000;
    private const uint MaxEvents = 1_024;

    public static EditorOpenRequest DecodeOpenRequest(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var value = new EditorOpenRequest(reader.ReadString(), reader.ReadString(), reader.ReadUInt32());
        reader.Complete();
        return value;
    }

    public static EditorEventRequest DecodeEventRequest(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var value = new EditorEventRequest(reader.ReadUInt64(), reader.ReadUInt32());
        reader.Complete();
        if (value.AfterSequence > long.MaxValue) PayloadFormat.Malformed("Invalid editor event sequence");
        if (value.Limit is 0 or > MaxEvents) PayloadFormat.Malformed("Invalid editor event limit");
        return value;
    }

    public static EditorCommand DecodeCommand(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var id = reader.ReadString();
        var kind = (EditorCommandKind)reader.ReadUInt32();
        var entities = ReadEntityIds(ref reader);
        var transform = reader.ReadBoolean() ? ReadTransform(ref reader) : null;
        var transformCount = reader.ReadUInt32();
        if (transformCount > MaxEntities) PayloadFormat.Malformed("Transform list exceeds item limit");
        var transforms = new EditorTransformUpdate[transformCount];
        for (var index = 0; index < transforms.Length; index++)
            transforms[index] = new(EntityId.Parse(reader.ReadString()), ReadTransform(ref reader));
        var text = reader.ReadBoolean() ? reader.ReadString() : null;
        var state = reader.ReadBoolean() ? ReadStateChange(ref reader) : null;
        reader.Complete();
        return new(id, kind, entities, transform, text, state, transforms);
    }

    public static byte[] EncodeSnapshot(EditorSnapshot value)
    {
        var writer = new PayloadWriter();
        writer.WriteString(value.ProjectPath);
        writer.WriteString(value.ProjectId.ToString());
        writer.WriteString(value.ProjectName);
        WriteTarget(writer, value.Target);
        WriteBaseLevel(writer, value.BaseLevel);
        if (value.Entities.Count > MaxEntities) PayloadFormat.Malformed("Entity list exceeds item limit");
        writer.WriteUInt32((uint)value.Entities.Count);
        foreach (var entity in value.Entities) WriteEntity(writer, entity);
        WriteEntityIds(writer, value.Selection);
        writer.WriteBoolean(value.IsDirty);
        writer.WriteBoolean(value.MigrationPending);
        writer.WriteBoolean(value.CanUndo);
        writer.WriteBoolean(value.CanRedo);
        writer.WriteBoolean(value.CanPaste);
        writer.WriteUInt64(checked((ulong)value.LastEventSequence));
        writer.WriteStrings(value.Capabilities.ToArray());
        writer.WriteUInt32((uint)value.Tools.Count);
        foreach (var tool in value.Tools)
        {
            writer.WriteString(tool.Id);
            writer.WriteString(tool.Label);
            writer.WriteString(tool.Capability);
        }
        writer.WriteUInt32((uint)value.Diagnostics.Count);
        foreach (var diagnostic in value.Diagnostics)
        {
            writer.WriteString(diagnostic.Code);
            writer.WriteUInt32((uint)diagnostic.Severity);
            writer.WriteString(diagnostic.Message);
        }
        return writer.ToArray();
    }

    public static byte[] EncodeEvents(IReadOnlyList<EditorEvent> values)
    {
        if (values.Count > MaxEvents) PayloadFormat.Malformed("Event list exceeds item limit");
        var writer = new PayloadWriter();
        writer.WriteUInt32((uint)values.Count);
        foreach (var value in values)
        {
            writer.WriteUInt64(checked((ulong)value.Sequence));
            writer.WriteUInt64(checked((ulong)value.CreatedUnixMilliseconds));
            writer.WriteUInt32((uint)value.Kind);
            writer.WriteBoolean(value.CommandId is not null);
            if (value.CommandId is not null) writer.WriteString(value.CommandId);
            WriteEntityIds(writer, value.EntityIds);
            writer.WriteBoolean(value.Message is not null);
            if (value.Message is not null) writer.WriteString(value.Message);
        }
        return writer.ToArray();
    }

    private static void WriteTarget(PayloadWriter writer, ProjectTargetProfile value)
    {
        writer.WriteString(value.Game);
        writer.WriteString(value.Region);
        writer.WriteString(value.Revision);
        writer.WriteString(value.BakeProfile);
    }

    private static void WriteBaseLevel(PayloadWriter writer, ProjectBaseLevel value)
    {
        writer.WriteString(value.Game);
        writer.WriteString(value.Region);
        writer.WriteString(value.Revision);
        writer.WriteUInt32(checked((uint)value.Level));
        writer.WriteString(value.SourceFingerprint);
        writer.WriteUInt32(checked((uint)value.MissingAssetCount));
    }

    private static void WriteEntity(PayloadWriter writer, EditorEntitySnapshot value)
    {
        writer.WriteString(value.EntityId.ToString());
        writer.WriteString(value.Name);
        writer.WriteString(value.Layer);
        WriteTransform(writer, value.Transform);
        writer.WriteBoolean(value.Asset is not null);
        if (value.Asset is not null)
        {
            writer.WriteString(value.Asset.Id.ToString());
            writer.WriteString(value.Asset.Kind.ToString());
        }
        writer.WriteBoolean(value.Provenance is not null);
        if (value.Provenance is not null)
        {
            writer.WriteString(value.Provenance.Game);
            writer.WriteUInt32(checked((uint)value.Provenance.Level));
            writer.WriteString(value.Provenance.Section);
            writer.WriteUInt32(checked((uint)value.Provenance.SourceIndex));
        }
        writer.WriteBoolean(value.SourceClassId is not null);
        if (value.SourceClassId is not null) writer.WriteUInt32(checked((uint)value.SourceClassId));
        writer.WriteBoolean(value.Geometry is not null);
        if (value.Geometry is not null)
        {
            writer.WriteUInt32((uint)value.Geometry.Kind);
            if (value.Geometry.Points.Count > MaxEntities) PayloadFormat.Malformed("Geometry point list exceeds item limit");
            writer.WriteUInt32((uint)value.Geometry.Points.Count);
            foreach (var point in value.Geometry.Points)
            {
                writer.WriteSingle(point.X);
                writer.WriteSingle(point.Y);
                writer.WriteSingle(point.Z);
                writer.WriteSingle(point.W);
            }
        }
        writer.WriteBoolean(value.State.Dirty);
        writer.WriteBoolean(value.State.Hidden);
        writer.WriteBoolean(value.State.Disabled);
        writer.WriteBoolean(value.State.Locked);
        writer.WriteBoolean(value.State.ReadOnly);
        writer.WriteBoolean(value.State.Invalid);
        writer.WriteBoolean(value.State.MissingAsset);
    }

    private static EditorEntityStateChange ReadStateChange(ref PayloadReader reader) => new(
        reader.ReadBoolean() ? reader.ReadBoolean() : null,
        reader.ReadBoolean() ? reader.ReadBoolean() : null,
        reader.ReadBoolean() ? reader.ReadBoolean() : null);

    private static void WriteTransform(PayloadWriter writer, ProjectTransform value)
    {
        WriteVector(writer, value.Position);
        writer.WriteSingle(value.Rotation.X);
        writer.WriteSingle(value.Rotation.Y);
        writer.WriteSingle(value.Rotation.Z);
        writer.WriteSingle(value.Rotation.W);
        WriteVector(writer, value.Scale);
    }

    private static ProjectTransform ReadTransform(ref PayloadReader reader) => new(
        ReadVector(ref reader),
        new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
        ReadVector(ref reader));

    private static void WriteVector(PayloadWriter writer, ProjectVector3 value)
    {
        writer.WriteSingle(value.X);
        writer.WriteSingle(value.Y);
        writer.WriteSingle(value.Z);
    }

    private static ProjectVector3 ReadVector(ref PayloadReader reader) =>
        new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    private static void WriteEntityIds(PayloadWriter writer, IReadOnlyList<EntityId> values)
    {
        if (values.Count > MaxEntities) PayloadFormat.Malformed("Entity ID list exceeds item limit");
        writer.WriteUInt32((uint)values.Count);
        foreach (var value in values) writer.WriteString(value.ToString());
    }

    private static EntityId[] ReadEntityIds(ref PayloadReader reader)
    {
        var count = reader.ReadUInt32();
        if (count > MaxEntities) PayloadFormat.Malformed("Entity ID list exceeds item limit");
        var values = new EntityId[count];
        for (var index = 0; index < values.Length; index++) values[index] = EntityId.Parse(reader.ReadString());
        return values;
    }
}
