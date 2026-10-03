using Forge.Host.Domain;

namespace Forge.Host.Bridge;

internal readonly record struct EditorOpenRequest(string ProjectPath, string CatalogRootPath, uint AutosaveSeconds);
internal readonly record struct EditorEventRequest(ulong AfterSequence, uint Limit);
internal readonly record struct EditorTieCollisionPreviewRequest(
    EntityId EntityId,
    EditorTieCollisionGenerationSettings? Settings);
internal readonly record struct EditorTieCollisionApplyRequest(string CommandId, string Token);
internal readonly record struct EditorTieCollisionRenderRequest(
    string CacheRootPath,
    string CatalogRootPath,
    string Token);

internal static class EditorPayloadCodec
{
    public static EntityId DecodeTieCollisionSourceRequest(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var entityId = EntityId.Parse(reader.ReadString());
        reader.Complete();
        return entityId;
    }

    public static byte[] EncodeTieCollisionSourceInfo(EditorTieCollisionSourceInfo value)
    {
        var writer = new PayloadWriter();
        writer.WriteString(value.TieAssetId.ToString());
        writer.WriteUInt32(checked((uint)value.SurfaceLodIndices.Count));
        foreach (var lodIndex in value.SurfaceLodIndices)
            writer.WriteUInt32(checked((uint)lodIndex));
        return writer.ToArray();
    }

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

    public static EditorTieCollisionPreviewRequest DecodeTieCollisionPreviewRequest(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var entityId = EntityId.Parse(reader.ReadString());
        EditorTieCollisionGenerationSettings? settings = null;
        if (reader.ReadBoolean())
        {
            var rawType = reader.ReadUInt32();
            var profileSections = reader.ReadUInt32();
            var encodedSurfaceLodIndex = reader.ReadUInt32();
            var useHull = reader.ReadBoolean();
            if (rawType > byte.MaxValue)
                PayloadFormat.Malformed("Invalid TIE collision raw type");
            if (profileSections is < 1 or > 16)
                PayloadFormat.Malformed("Invalid TIE collision profile sections");
            if (encodedSurfaceLodIndex > 3)
                PayloadFormat.Malformed("Invalid TIE collision surface LOD");
            settings = new(
                (byte)rawType, checked((int)profileSections),
                checked((int)encodedSurfaceLodIndex) - 1, useHull);
        }
        reader.Complete();
        return new(entityId, settings);
    }

    public static EditorTieCollisionApplyRequest DecodeTieCollisionApplyRequest(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var value = new EditorTieCollisionApplyRequest(reader.ReadString(), reader.ReadString());
        reader.Complete();
        return value;
    }

    public static EditorTieCollisionRenderRequest DecodeTieCollisionRenderRequest(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var value = new EditorTieCollisionRenderRequest(
            reader.ReadString(), reader.ReadString(), reader.ReadString());
        reader.Complete();
        return value;
    }

    public static byte[] EncodeTieCollisionPreview(EditorTieCollisionPreview value)
    {
        var writer = new PayloadWriter();
        writer.WriteString(value.TieAssetId.ToString());
        writer.WriteUInt32(checked((uint)value.Candidates.Count));
        foreach (var candidate in value.Candidates)
        {
            writer.WriteString(candidate.Token);
            writer.WriteUInt32((uint)candidate.Preset);
            writer.WriteString(candidate.Label);
            WriteTieCollisionRecipe(writer, candidate.Recipe);
            writer.WriteUInt32(checked((uint)candidate.EncodedByteCount));
            writer.WriteUInt32(checked((uint)candidate.VertexCount));
            writer.WriteUInt32(checked((uint)candidate.FaceCount));
            writer.WriteUInt32(checked((uint)candidate.OccupiedOctantCount));
            writer.WriteUInt32(checked((uint)candidate.DuplicateFaceCount));
            writer.WriteUInt32(checked((uint)candidate.HardViolationCount));
            writer.WriteSingle(candidate.MaximumDeviation);
            writer.WriteUInt32(checked((uint)candidate.DeviationSampleCount));
            WriteCollisionOctants(writer, candidate.Octants);
            writer.WriteBoolean(candidate.CombinedAnalysis is not null);
            if (candidate.CombinedAnalysis is { } combined)
            {
                writer.WriteUInt32(checked((uint)combined.InstanceCount));
                writer.WriteUInt32(checked((uint)combined.LogicalFaceCount));
                writer.WriteUInt32(checked((uint)combined.OccupiedOctantCount));
                writer.WriteUInt32(checked((uint)combined.DuplicateFaceCount));
                writer.WriteUInt32(checked((uint)combined.HardViolationCount));
                WriteCollisionOctants(writer, combined.Octants);
                writer.WriteBoolean(combined.Error is not null);
                if (combined.Error is not null) writer.WriteString(combined.Error);
            }
        }
        return writer.ToArray();
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
        var levelSettings = reader.ReadBoolean() ? ReadLevelSettings(ref reader) : null;
        IReadOnlyList<ProjectVector4>? points = null;
        if (reader.ReadBoolean())
        {
            var pointCount = reader.ReadUInt32();
            if (pointCount > MaxEntities) PayloadFormat.Malformed("Spline point list exceeds item limit");
            var values = new ProjectVector4[pointCount];
            for (var index = 0; index < values.Length; index++)
                values[index] = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            points = values;
        }
        EditorAssetPlacement? placement = null;
        if (reader.ReadBoolean())
        {
            var assetId = AssetId.Parse(reader.ReadString());
            var assetKind = reader.ReadString() switch
            {
                "Tie" => AssetKind.Tie,
                "Shrub" => AssetKind.Shrub,
                "Moby" => AssetKind.Moby,
                _ => throw new InvalidDataException("Asset placement kind is invalid."),
            };
            placement = new(
                assetId,
                assetKind,
                checked((int)reader.ReadUInt32()),
                ReadTransform(ref reader));
        }
        var skyShellSource = reader.ReadBoolean()
            ? new EditorSkyShellSource(AssetId.Parse(reader.ReadString()), checked((int)reader.ReadUInt32()))
            : null;
        EditorSkyShellUpdate? skyShellUpdate = null;
        if (reader.ReadBoolean())
        {
            var initialRotation = reader.ReadBoolean() ? ReadVector(ref reader) : null;
            var angularVelocity = reader.ReadBoolean() ? ReadVector(ref reader) : null;
            skyShellUpdate = new(initialRotation, angularVelocity);
        }
        int? destinationOrder = reader.ReadBoolean() ? checked((int)reader.ReadUInt32()) : null;
        bool? tieCollisionEnabled = reader.ReadBoolean() ? reader.ReadBoolean() : null;
        byte? tieCollisionRawType = null;
        if (reader.ReadBoolean())
        {
            var value = reader.ReadUInt32();
            if (value > byte.MaxValue) PayloadFormat.Malformed("Invalid TIE collision raw type");
            tieCollisionRawType = (byte)value;
        }
        reader.Complete();
        return new(
            id, kind, entities, transform, text, state, transforms, levelSettings, points, placement,
            skyShellSource, skyShellUpdate, destinationOrder, tieCollisionEnabled, tieCollisionRawType);
    }

    public static byte[] EncodeSnapshot(EditorSnapshot value)
    {
        var writer = new PayloadWriter();
        writer.WriteString(value.ProjectPath);
        writer.WriteString(value.ProjectId.ToString());
        writer.WriteString(value.ProjectName);
        WriteTarget(writer, value.Target);
        WriteBaseLevel(writer, value.BaseLevel);
        writer.WriteBoolean(value.LevelSettings is not null);
        if (value.LevelSettings is not null) WriteLevelSettings(writer, value.LevelSettings);
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
        writer.WriteBoolean(value.SkyShell is not null);
        if (value.SkyShell is not null)
        {
            writer.WriteUInt32(checked((uint)value.SkyShell.SourceShellIndex));
            writer.WriteUInt32(checked((uint)value.SkyShell.Order));
            WriteVector(writer, value.SkyShell.InitialRotationRadians);
            WriteVector(writer, value.SkyShell.AngularVelocityRadiansPerSecond);
        }
        writer.WriteBoolean(value.Collision is not null);
        if (value.Collision is not null)
        {
            writer.WriteUInt32((uint)value.Collision.Kind);
            writer.WriteUInt32(checked((uint)value.Collision.SourcePayloadIndex));
            writer.WriteUInt32(checked((uint)value.Collision.SourcePieceIndex));
            writer.WriteUInt32(checked((uint)value.Collision.FaceCount));
            writer.WriteUInt32(checked((uint)value.Collision.VertexCount));
            writer.WriteUInt32(checked((uint)value.Collision.Types.Count));
            foreach (var type in value.Collision.Types)
            {
                writer.WriteUInt32(type.RawType);
                writer.WriteUInt32(checked((uint)type.Count));
            }
        }
        writer.WriteBoolean(value.TieCollision is not null);
        if (value.TieCollision is not null)
        {
            writer.WriteString(value.TieCollision.ProxyAssetId.ToString());
            WriteTieCollisionRecipe(writer, value.TieCollision.Recipe);
        }
        writer.WriteBoolean(value.TieCollisionEnabled is not null);
        if (value.TieCollisionEnabled is not null) writer.WriteBoolean(value.TieCollisionEnabled.Value);
        writer.WriteUInt32((uint)value.TransformCapabilities);
        writer.WriteBoolean(value.State.Dirty);
        writer.WriteBoolean(value.State.Hidden);
        writer.WriteBoolean(value.State.Disabled);
        writer.WriteBoolean(value.State.Locked);
        writer.WriteBoolean(value.State.ReadOnly);
        writer.WriteBoolean(value.State.Invalid);
        writer.WriteBoolean(value.State.MissingAsset);
    }

    private static void WriteTieCollisionRecipe(PayloadWriter writer, ProjectTieCollisionRecipe value)
    {
        writer.WriteUInt32((uint)value.Kind);
        writer.WriteUInt32(checked((uint)value.GeneratorVersion));
        writer.WriteUInt32(checked((uint)value.RecipeVersion));
        writer.WriteUInt32(checked((uint)value.LodIndex));
        writer.WriteUInt32(value.RawType);
        writer.WriteSingle(value.DetailSize);
        writer.WriteSingle(value.SealOpeningSize);
        writer.WriteSingle(value.SurfaceOffset);
        writer.WriteBoolean(value.OpenBase);
        writer.WriteUInt32(checked((uint)value.ProfileSections));
    }

    private static void WriteCollisionOctants(
        PayloadWriter writer,
        IReadOnlyList<EditorCollisionOctantCost> octants)
    {
        writer.WriteUInt32(checked((uint)octants.Count));
        foreach (var octant in octants)
        {
            writer.WriteUInt32(unchecked((uint)octant.X));
            writer.WriteUInt32(unchecked((uint)octant.Y));
            writer.WriteUInt32(unchecked((uint)octant.Z));
            writer.WriteUInt32(checked((uint)octant.FaceCount));
            writer.WriteUInt32(checked((uint)octant.VertexCount));
            writer.WriteUInt32(checked((uint)octant.QuadCount));
            writer.WriteUInt32(checked((uint)octant.EncodedByteCount));
            writer.WriteStrings(octant.Violations.ToArray());
            writer.WriteStrings((octant.AdditionIds ?? []).Take(PayloadFormat.MaxListItems).ToArray());
        }
    }

    private static EditorEntityStateChange ReadStateChange(ref PayloadReader reader) => new(
        reader.ReadBoolean() ? reader.ReadBoolean() : null,
        reader.ReadBoolean() ? reader.ReadBoolean() : null,
        reader.ReadBoolean() ? reader.ReadBoolean() : null);

    private static ProjectLevelSettings ReadLevelSettings(ref PayloadReader reader) => new(
        new(checked((byte)reader.ReadUInt32()), checked((byte)reader.ReadUInt32()), checked((byte)reader.ReadUInt32())),
        new(checked((byte)reader.ReadUInt32()), checked((byte)reader.ReadUInt32()), checked((byte)reader.ReadUInt32())),
        reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    private static void WriteLevelSettings(PayloadWriter writer, ProjectLevelSettings value)
    {
        writer.WriteUInt32(value.BackgroundColor.R);
        writer.WriteUInt32(value.BackgroundColor.G);
        writer.WriteUInt32(value.BackgroundColor.B);
        writer.WriteUInt32(value.FogColor.R);
        writer.WriteUInt32(value.FogColor.G);
        writer.WriteUInt32(value.FogColor.B);
        writer.WriteSingle(value.FogNearDistance);
        writer.WriteSingle(value.FogFarDistance);
        writer.WriteSingle(value.FogNearIntensity);
        writer.WriteSingle(value.FogFarIntensity);
    }

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
