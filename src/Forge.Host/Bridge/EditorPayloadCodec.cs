using Forge.Host.Domain;

namespace Forge.Host.Bridge;

internal readonly record struct EditorOpenRequest(string ProjectPath, string CatalogRootPath, uint AutosaveSeconds);
internal readonly record struct EditorEventRequest(ulong AfterSequence, uint Limit);
internal readonly record struct EditorInstancedCollisionPreviewRequest(
    EntityId EntityId,
    EditorInstancedCollisionGenerationSettings? Settings);
internal readonly record struct EditorInstancedCollisionApplyRequest(string CommandId, string Token);
internal readonly record struct EditorInstancedCollisionRenderRequest(
    string CacheRootPath,
    string CatalogRootPath,
    string Token);

internal static class EditorPayloadCodec
{
    public static EntityId DecodeInstancedCollisionSourceRequest(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var entityId = EntityId.Parse(reader.ReadString());
        reader.Complete();
        return entityId;
    }

    public static byte[] EncodeInstancedCollisionSourceInfo(EditorInstancedCollisionSourceInfo value)
    {
        var writer = new PayloadWriter();
        writer.WriteString(value.SourceAssetId.ToString());
        writer.WriteUInt32(checked((uint)value.SurfaceLodIndices.Count));
        foreach (var lodIndex in value.SurfaceLodIndices)
            writer.WriteUInt32(checked((uint)lodIndex));
        return writer.ToArray();
    }

    private const uint MaxEntities = 100_000;
    private const uint MaxReferences = 1_000_000;
    private const uint MaxEvents = 1_024;
    private const int MaxHudImageBytes = 16 * 1024 * 1024;
    private const int MaxFxImageBytes = 16 * 1024 * 1024;

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

    public static EditorInstancedCollisionPreviewRequest DecodeInstancedCollisionPreviewRequest(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var entityId = EntityId.Parse(reader.ReadString());
        EditorInstancedCollisionGenerationSettings? settings = null;
        if (reader.ReadBoolean())
        {
            var rawType = reader.ReadUInt32();
            var profileSections = reader.ReadUInt32();
            var encodedSurfaceLodIndex = reader.ReadUInt32();
            var useHull = reader.ReadBoolean();
            if (rawType > byte.MaxValue)
                PayloadFormat.Malformed("Invalid instanced collision raw type");
            if (profileSections is < 1 or > 16)
                PayloadFormat.Malformed("Invalid instanced collision profile sections");
            if (encodedSurfaceLodIndex > 3)
                PayloadFormat.Malformed("Invalid instanced collision surface LOD");
            settings = new(
                (byte)rawType, checked((int)profileSections),
                checked((int)encodedSurfaceLodIndex) - 1, useHull);
        }
        reader.Complete();
        return new(entityId, settings);
    }

    public static EditorInstancedCollisionApplyRequest DecodeInstancedCollisionApplyRequest(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var value = new EditorInstancedCollisionApplyRequest(reader.ReadString(), reader.ReadString());
        reader.Complete();
        return value;
    }

    public static EditorInstancedCollisionRenderRequest DecodeInstancedCollisionRenderRequest(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var value = new EditorInstancedCollisionRenderRequest(
            reader.ReadString(), reader.ReadString(), reader.ReadString());
        reader.Complete();
        return value;
    }

    public static byte[] EncodeInstancedCollisionPreview(EditorInstancedCollisionPreview value)
    {
        var writer = new PayloadWriter();
        writer.WriteString(value.SourceAssetId.ToString());
        writer.WriteUInt32(checked((uint)value.Candidates.Count));
        foreach (var candidate in value.Candidates)
        {
            writer.WriteString(candidate.Token);
            writer.WriteUInt32((uint)candidate.Preset);
            writer.WriteString(candidate.Label);
            WriteInstancedCollisionRecipe(writer, candidate.Recipe);
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
        bool? instancedCollisionEnabled = reader.ReadBoolean() ? reader.ReadBoolean() : null;
        byte? instancedCollisionRawType = null;
        if (reader.ReadBoolean())
        {
            var value = reader.ReadUInt32();
            if (value > byte.MaxValue) PayloadFormat.Malformed("Invalid instanced collision raw type");
            instancedCollisionRawType = (byte)value;
        }
        AssetId? instancedCollisionProxyAssetId = null;
        IReadOnlyList<ProjectCollisionFaceTypeOverride>? instancedCollisionFaceTypes = null;
        if (reader.ReadBoolean())
        {
            instancedCollisionProxyAssetId = AssetId.Parse(reader.ReadString());
            var count = reader.ReadUInt32();
            if (count > ForgeProjectValidation.MaxInstancedCollisionFaceTypeOverrides)
                PayloadFormat.Malformed("Instanced collision face-type list exceeds item limit");
            var values = new ProjectCollisionFaceTypeOverride[count];
            for (var index = 0; index < values.Length; index++)
            {
                var faceIndex = reader.ReadUInt32();
                var rawType = reader.ReadUInt32();
                if (faceIndex > int.MaxValue || rawType > byte.MaxValue)
                    PayloadFormat.Malformed("Invalid instanced collision face-type assignment");
                values[index] = new((int)faceIndex, (byte)rawType);
            }
            instancedCollisionFaceTypes = values;
        }
        EditorReferenceUpdate? referenceUpdate = null;
        if (reader.ReadBoolean())
        {
            var fieldKey = reader.ReadString();
            int? sourceValue = reader.ReadBoolean() ? checked((int)reader.ReadUInt32()) : null;
            EntityId? targetEntityId = reader.ReadBoolean() ? EntityId.Parse(reader.ReadString()) : null;
            referenceUpdate = new(fieldKey, sourceValue, targetEntityId);
        }
        ProjectPaletteOptimization? paletteOptimization = null;
        if (reader.ReadBoolean())
            paletteOptimization = new(reader.ReadString(), checked((int)reader.ReadUInt32()));
        EditorHudEdit? hudEdit = null;
        if (reader.ReadBoolean())
        {
            var sourceAssetId = reader.ReadBoolean() ? AssetId.Parse(reader.ReadString()) : (AssetId?)null;
            ushort? spriteId = null;
            if (reader.ReadBoolean())
            {
                var value = reader.ReadUInt32();
                if (value > ushort.MaxValue) PayloadFormat.Malformed("Invalid HUD sprite ID");
                spriteId = (ushort)value;
            }
            int? bankIndex = reader.ReadBoolean() ? checked((int)reader.ReadUInt32()) : null;
            var imageFormat = reader.ReadBoolean() ? reader.ReadString() : null;
            var imageBytes = reader.ReadBoolean() ? reader.ReadBytes(MaxHudImageBytes) : null;
            hudEdit = new(sourceAssetId, spriteId, bankIndex, imageFormat, imageBytes);
        }
        EditorFxEdit? fxEdit = null;
        if (reader.ReadBoolean())
        {
            var sourceAssetId = reader.ReadBoolean() ? AssetId.Parse(reader.ReadString()) : (AssetId?)null;
            int? index = reader.ReadBoolean() ? checked((int)reader.ReadUInt32()) : null;
            var imageFormat = reader.ReadBoolean() ? reader.ReadString() : null;
            var imageBytes = reader.ReadBoolean() ? reader.ReadBytes(MaxFxImageBytes) : null;
            fxEdit = new(sourceAssetId, index, imageFormat, imageBytes);
        }
        reader.Complete();
        return new(
            id, kind, entities, transform, text, state, transforms, levelSettings, points, placement,
            skyShellSource, skyShellUpdate, destinationOrder, instancedCollisionEnabled, instancedCollisionRawType,
            instancedCollisionProxyAssetId, instancedCollisionFaceTypes, referenceUpdate, paletteOptimization, hudEdit,
            fxEdit);
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
        writer.WriteBoolean(value.Hud is not null);
        if (value.Hud is not null) WriteHud(writer, value.Hud);
        writer.WriteBoolean(value.Fx is not null);
        if (value.Fx is not null) WriteFx(writer, value.Fx);
        if (value.Entities.Count > MaxEntities) PayloadFormat.Malformed("Entity list exceeds item limit");
        writer.WriteUInt32((uint)value.Entities.Count);
        foreach (var entity in value.Entities) WriteEntity(writer, entity);
        if (value.References.Count > MaxReferences) PayloadFormat.Malformed("Reference list exceeds item limit");
        writer.WriteUInt32((uint)value.References.Count);
        foreach (var reference in value.References) WriteReference(writer, reference);
        WriteEntityIds(writer, value.Selection);
        writer.WriteBoolean(value.IsDirty);
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

    private static void WriteHud(PayloadWriter writer, EditorHudSnapshot value)
    {
        writer.WriteBoolean(value.CanRead);
        writer.WriteBoolean(value.IsDirty);
        writer.WriteBoolean(value.CanReplace);
        writer.WriteBoolean(value.CanAppend);
        writer.WriteString(value.AuthoringDisabledReason ?? string.Empty);
        writer.WriteUInt32(checked((uint)value.PhysicalBankCount));
        writer.WriteUInt32(checked((uint)value.MinimumAppendBank));
        writer.WriteUInt32(value.MinimumAppendSpriteId);
        writer.WriteUInt32(value.MaximumAppendSpriteId);
        writer.WriteUInt32(checked((uint)value.MaximumIconCount));
        writer.WriteUInt32(checked((uint)value.SourceIcons.Count));
        foreach (var icon in value.SourceIcons)
        {
            writer.WriteUInt32(checked((uint)icon.SourceIconIndex));
            writer.WriteUInt32(icon.SpriteId);
            writer.WriteUInt32(checked((uint)icon.Frames.Count));
            foreach (var frame in icon.Frames)
            {
                writer.WriteUInt32(checked((uint)frame.SourceFrameIndex));
                writer.WriteInt32(frame.SourcePaletteIndex);
                writer.WriteInt32(frame.SourceTextureIndex);
                writer.WriteInt32(frame.PaletteBankIndex);
                writer.WriteInt32(frame.TextureBankIndex);
                writer.WriteInt32(frame.Width);
                writer.WriteInt32(frame.Height);
                WriteAssetReference(writer, frame.SourceTexture);
                WriteAssetReference(writer, frame.EffectiveTexture);
                writer.WriteString(frame.Diagnostic ?? string.Empty);
            }
        }
        writer.WriteUInt32(checked((uint)value.Additions.Count));
        foreach (var addition in value.Additions)
        {
            writer.WriteUInt32(addition.SpriteId);
            writer.WriteUInt32(checked((uint)addition.BankIndex));
            writer.WriteUInt32(checked((uint)addition.Width));
            writer.WriteUInt32(checked((uint)addition.Height));
            WriteAssetReference(writer, addition.Texture);
        }
    }

    private static void WriteAssetReference(PayloadWriter writer, ProjectAssetReference? value)
    {
        writer.WriteBoolean(value is not null);
        if (value is null) return;
        writer.WriteString(value.Id.ToString());
        writer.WriteString(value.Kind.ToString());
    }

    private static void WriteFx(PayloadWriter writer, EditorFxSnapshot value)
    {
        writer.WriteBoolean(value.CanRead);
        writer.WriteBoolean(value.IsDirty);
        writer.WriteBoolean(value.CanReplace);
        writer.WriteBoolean(value.CanAppend);
        writer.WriteString(value.AuthoringDisabledReason ?? string.Empty);
        writer.WriteUInt32(checked((uint)value.MaximumTextureCount));
        writer.WriteUInt32(checked((uint)value.SourceTextures.Count));
        foreach (var texture in value.SourceTextures)
        {
            writer.WriteUInt32(checked((uint)texture.SourceIndex));
            writer.WriteString(texture.Label);
            writer.WriteInt32(texture.Width);
            writer.WriteInt32(texture.Height);
            writer.WriteInt32(texture.PaletteOffset);
            writer.WriteInt32(texture.PixelOffset);
            writer.WriteBoolean(texture.IsSwizzled);
            WriteAssetReference(writer, texture.SourceTexture);
            WriteAssetReference(writer, texture.EffectiveTexture);
            writer.WriteString(texture.Diagnostic ?? string.Empty);
        }
        writer.WriteUInt32(checked((uint)value.Additions.Count));
        foreach (var addition in value.Additions)
        {
            writer.WriteUInt32(checked((uint)addition.Width));
            writer.WriteUInt32(checked((uint)addition.Height));
            WriteAssetReference(writer, addition.Texture);
        }
    }

    private static void WriteReference(PayloadWriter writer, EditorReferenceSnapshot value)
    {
        writer.WriteString(value.OwnerEntityId.ToString());
        writer.WriteUInt32((uint)value.Reference.Domain);
        writer.WriteString(value.Reference.FieldKey);
        writer.WriteBoolean(value.Reference.Nullable);
        switch (value.Reference.Domain)
        {
            case ProjectReferenceDomain.Entity when value.Reference.EntityKind is { } entityKind:
                writer.WriteUInt32((uint)entityKind);
                writer.WriteBoolean(value.Reference.EntityId is not null);
                if (value.Reference.EntityId is { } entityId) writer.WriteString(entityId.ToString());
                break;
            case ProjectReferenceDomain.Asset when value.Reference.AssetKind is { } assetKind:
                writer.WriteString(assetKind.ToString());
                writer.WriteBoolean(value.Reference.AssetId is not null);
                if (value.Reference.AssetId is { } assetId) writer.WriteString(assetId.ToString());
                break;
            default:
                PayloadFormat.Malformed($"Invalid {value.Reference.Domain} reference contract");
                break;
        }
        writer.WriteBoolean(value.SourceValue is not null);
        if (value.SourceValue is { } sourceValue) writer.WriteUInt32(checked((uint)sourceValue));
        writer.WriteBoolean(value.Missing);
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
        writer.WriteString(value.PaletteOptimization.MappingVersion);
        writer.WriteUInt32(checked((uint)value.PaletteOptimization.Strength));
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
            writer.WriteBoolean(value.Collision.Attachment is not null);
            if (value.Collision.Attachment is not null)
            {
                writer.WriteString(value.Collision.Attachment.ParentEntityId.ToString());
                WriteTransform(writer, value.Collision.Attachment.BindTransform);
            }
        }
        writer.WriteBoolean(value.InstancedCollision is not null);
        if (value.InstancedCollision is not null)
        {
            writer.WriteString(value.InstancedCollision.ProxyAssetId.ToString());
            WriteInstancedCollisionRecipe(writer, value.InstancedCollision.Recipe);
            writer.WriteUInt32(checked((uint)value.InstancedCollision.FaceTypeOverrides.Count));
            foreach (var faceType in value.InstancedCollision.FaceTypeOverrides)
            {
                writer.WriteUInt32(checked((uint)faceType.FaceIndex));
                writer.WriteUInt32(faceType.RawType);
            }
        }
        writer.WriteBoolean(value.IndividualInstancedCollision is not null);
        if (value.IndividualInstancedCollision is not null)
        {
            writer.WriteString(value.IndividualInstancedCollision.ProxyAssetId.ToString());
            WriteInstancedCollisionRecipe(writer, value.IndividualInstancedCollision.Recipe);
            writer.WriteUInt32(checked((uint)value.IndividualInstancedCollision.FaceTypeOverrides.Count));
            foreach (var faceType in value.IndividualInstancedCollision.FaceTypeOverrides)
            {
                writer.WriteUInt32(checked((uint)faceType.FaceIndex));
                writer.WriteUInt32(faceType.RawType);
            }
        }
        writer.WriteBoolean(value.InstancedCollisionEnabled is not null);
        if (value.InstancedCollisionEnabled is not null) writer.WriteBoolean(value.InstancedCollisionEnabled.Value);
        writer.WriteUInt32((uint)value.TransformCapabilities);
        writer.WriteBoolean(value.State.Dirty);
        writer.WriteBoolean(value.State.Hidden);
        writer.WriteBoolean(value.State.Disabled);
        writer.WriteBoolean(value.State.Locked);
        writer.WriteBoolean(value.State.ReadOnly);
        writer.WriteBoolean(value.State.Invalid);
        writer.WriteBoolean(value.State.MissingAsset);
    }

    private static void WriteInstancedCollisionRecipe(PayloadWriter writer, ProjectInstancedCollisionRecipe value)
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
