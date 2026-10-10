using System.Buffers.Binary;
using System.Globalization;
using Forge.Host.Domain;

namespace Forge.Host.Games.UYA;

internal static partial class UyaMobyPVarEditorService
{
    public static Task ExecuteAsync(
        ForgeProjectWorkspace workspace,
        EditorCommand command,
        MobyDexCatalog catalog,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (command.Kind == EditorCommandKind.InitializeMobyPVar)
        {
            UyaMobyPVarService.CreateDefault(workspace, catalog, command.EntityIds.Single());
            return Task.CompletedTask;
        }
        ApplyEdit(workspace, catalog, command.EntityIds.Single(), command.MobyPVarEdit!);
        return Task.CompletedTask;
    }

    private static void ApplyEdit(
        ForgeProjectWorkspace workspace,
        MobyDexCatalog catalog,
        EntityId entityId,
        EditorMobyPVarEdit edit)
    {
        var entity = workspace.Content.Entities.Single(value => value.EntityId == entityId);
        if (entity.Layer != "mobys" || entity.Source?.ClassId != edit.ExpectedClassId)
            throw new InvalidOperationException("The selected moby no longer matches the PVar edit.");
        var resolved = workspace.ResolveMobyDexEntry(catalog, "UYA", edit.ExpectedClassId)
            ?? throw new InvalidOperationException("The active MobyDex entry is no longer available.");
        if (resolved.DatasetId != edit.ExpectedDatasetId
            || resolved.DatasetVersion != edit.ExpectedDatasetVersion
            || resolved.SchemaVersion != edit.ExpectedSchemaVersion
            || resolved.Fingerprint != edit.ExpectedSchemaFingerprint)
            throw new InvalidOperationException("The MobyDex schema changed. Refresh the field before editing it.");
        var definition = resolved.Entry.PVar
            ?? throw new InvalidOperationException("The active MobyDex entry no longer defines a PVar.");
        var pvar = workspace.Content.MobyPVars?.Entries.SingleOrDefault(value => value.EntityId == entityId)
            ?? throw new InvalidOperationException("The selected moby no longer has PVar data.");
        if (pvar.Data.Length != definition.Length || Fingerprint(pvar) != edit.ExpectedStateFingerprint)
            throw new InvalidOperationException("The PVar changed. Refresh the field before editing it.");
        var field = Leaves(definition.Fields, 0, string.Empty).SingleOrDefault(value => value.Path == edit.FieldPath)
            ?? throw new InvalidOperationException("The active schema no longer contains that PVar field.");
        var data = pvar.Data.ToArray();
        var references = pvar.References.ToList();
        WriteValue(field, edit.Value, data, references, workspace.Content.Entities);
        workspace.ReplaceMobyPVar(pvar with { Data = data, References = references.ToArray() });
    }

    private static void WriteValue(
        Leaf field,
        EditorMobyPVarValue value,
        byte[] data,
        List<ProjectMobyPVarReference> references,
        IReadOnlyList<ProjectEntity> entities)
    {
        var span = data.AsSpan(field.Offset, field.Length);
        switch (field.Definition.Kind)
        {
            case "int32": WriteSigned(field, value, span); break;
            case "uint8": WriteUnsigned(field, value, span, byte.MaxValue); break;
            case "uint64": WriteUnsigned(field, value, span, ulong.MaxValue); break;
            case "enum":
            case "flags": WriteStoredInteger(field, value, span); break;
            case "float32": WriteFloat(field, value, span, single: true); break;
            case "float64": WriteFloat(field, value, span, single: false); break;
            case "bool8":
                Require(value, EditorMobyPVarValueKind.Boolean);
                span[0] = value.Boolean!.Value ? (byte)1 : (byte)0;
                break;
            case "rgb8": WriteColor(field, value, span, 3); break;
            case "rgba8": WriteColor(field, value, span, 4); break;
            case "vector2f32": WriteVector(field, value, span, 2); break;
            case "vector3f32": WriteVector(field, value, span, 3); break;
            case "reference": WriteReference(field, value, span, references, entities); break;
            default: throw new InvalidOperationException($"PVar field {field.Path} is not editable.");
        }
    }

    private static void WriteSigned(Leaf field, EditorMobyPVarValue value, Span<byte> target)
    {
        Require(value, EditorMobyPVarValueKind.Integer);
        if (!int.TryParse(value.Integer, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            throw new InvalidOperationException($"PVar field {field.Path} requires a signed 32-bit integer.");
        ValidateRange(field, parsed);
        BinaryPrimitives.WriteInt32LittleEndian(target, parsed);
    }

    private static void WriteUnsigned(
        Leaf field,
        EditorMobyPVarValue value,
        Span<byte> target,
        ulong maximum)
    {
        Require(value, EditorMobyPVarValueKind.Integer);
        if (!ulong.TryParse(value.Integer, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            || parsed > maximum)
            throw new InvalidOperationException($"PVar field {field.Path} requires an unsigned integer.");
        ValidateRange(field, parsed);
        if (target.Length == 1) target[0] = (byte)parsed;
        else BinaryPrimitives.WriteUInt64LittleEndian(target, parsed);
    }

    private static void WriteStoredInteger(Leaf field, EditorMobyPVarValue value, Span<byte> target)
    {
        Require(value, EditorMobyPVarValueKind.Integer);
        if (!long.TryParse(value.Integer, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            throw new InvalidOperationException($"PVar field {field.Path} requires an integer.");
        if (field.Definition.Kind == "enum"
            && field.Definition.Options?.Any(option => option.Value == parsed) != true)
            throw new InvalidOperationException($"PVar field {field.Path} requires a declared option.");
        switch (field.Definition.Storage)
        {
            case "uint8" when parsed is >= byte.MinValue and <= byte.MaxValue: target[0] = (byte)parsed; break;
            case "int32" when parsed is >= int.MinValue and <= int.MaxValue:
                BinaryPrimitives.WriteInt32LittleEndian(target, (int)parsed); break;
            case "uint32" when parsed is >= uint.MinValue and <= uint.MaxValue:
                BinaryPrimitives.WriteUInt32LittleEndian(target, (uint)parsed); break;
            default: throw new InvalidOperationException($"PVar field {field.Path} value does not fit its storage.");
        }
    }

    private static void WriteFloat(Leaf field, EditorMobyPVarValue value, Span<byte> target, bool single)
    {
        Require(value, EditorMobyPVarValueKind.Float);
        var parsed = value.Float!.Value;
        if (!double.IsFinite(parsed)) throw new InvalidOperationException("PVar floating-point values must be finite.");
        if (field.Minimum is { } minimum && parsed < (double)minimum
            || field.Maximum is { } maximum && parsed > (double)maximum)
            throw new InvalidOperationException($"PVar field {field.Path} is outside its declared range.");
        if (single)
        {
            var narrowed = (float)parsed;
            if (!float.IsFinite(narrowed)) throw new InvalidOperationException("PVar value exceeds float32 range.");
            BinaryPrimitives.WriteSingleLittleEndian(target, narrowed);
        }
        else BinaryPrimitives.WriteInt64LittleEndian(target, BitConverter.DoubleToInt64Bits(parsed));
    }

    private static void WriteColor(Leaf field, EditorMobyPVarValue value, Span<byte> target, int count)
    {
        Require(value, EditorMobyPVarValueKind.Color);
        if (value.Color?.Count != count) throw new InvalidOperationException($"PVar field {field.Path} requires {count} channels.");
        value.Color.ToArray().CopyTo(target);
    }

    private static void WriteVector(Leaf field, EditorMobyPVarValue value, Span<byte> target, int count)
    {
        Require(value, EditorMobyPVarValueKind.Vector);
        if (value.Vector?.Count != count || value.Vector.Any(component => !double.IsFinite(component)))
            throw new InvalidOperationException($"PVar field {field.Path} requires {count} finite components.");
        for (var index = 0; index < count; index++)
        {
            var component = (float)value.Vector[index];
            if (!float.IsFinite(component)) throw new InvalidOperationException("PVar vector component exceeds float32 range.");
            BinaryPrimitives.WriteSingleLittleEndian(target[(index * 4)..], component);
        }
    }

    private static void WriteReference(
        Leaf field,
        EditorMobyPVarValue value,
        Span<byte> target,
        List<ProjectMobyPVarReference> references,
        IReadOnlyList<ProjectEntity> entities)
    {
        Require(value, EditorMobyPVarValueKind.Reference);
        var targetKind = UyaMobyPVarService.TargetKind(field.Definition.TargetKind!);
        var targetEntity = value.Reference is { } id
            ? entities.SingleOrDefault(entity => entity.EntityId == id)
                ?? throw new InvalidOperationException("The selected PVar reference target no longer exists.")
            : null;
        if (targetEntity is not null && targetKind != ProjectEntityKind.Entity
            && ProjectReferences.KindOf(targetEntity) != targetKind)
            throw new InvalidOperationException("The selected entity is not compatible with the PVar reference.");
        var nullValue = checked((int)field.Definition.NullValue!.Value);
        var sourceValue = targetEntity?.Provenance?.SourceIndex ?? nullValue;
        BinaryPrimitives.WriteInt32LittleEndian(target, sourceValue);
        var reference = new ProjectMobyPVarReference(
            field.Offset, 4, nullValue, sourceValue,
            new(ProjectReferenceDomain.Entity, field.Path, true, targetKind, targetEntity?.EntityId));
        var index = references.FindIndex(candidate => candidate.Reference.FieldKey == field.Path);
        if (index < 0) references.Add(reference);
        else references[index] = reference;
    }

    private static void Require(EditorMobyPVarValue value, EditorMobyPVarValueKind kind)
    {
        var populated = (value.Integer is not null ? 1 : 0)
            + (value.Float is not null ? 1 : 0)
            + (value.Boolean is not null ? 1 : 0)
            + (value.Color is not null ? 1 : 0)
            + (value.Vector is not null ? 1 : 0)
            + (value.Bytes is not null ? 1 : 0)
            + (value.Reference is not null ? 1 : 0);
        var valid = value.Kind == kind && kind switch
        {
            EditorMobyPVarValueKind.Integer => value.Integer is not null && populated == 1,
            EditorMobyPVarValueKind.Float => value.Float is not null && populated == 1,
            EditorMobyPVarValueKind.Boolean => value.Boolean is not null && populated == 1,
            EditorMobyPVarValueKind.Color => value.Color is not null && populated == 1,
            EditorMobyPVarValueKind.Vector => value.Vector is not null && populated == 1,
            EditorMobyPVarValueKind.Reference => populated == (value.Reference is null ? 0 : 1),
            _ => false,
        };
        if (!valid) throw new InvalidOperationException("The PVar value does not match the field type.");
    }

    private static void ValidateRange(Leaf field, decimal value)
    {
        if (field.Minimum is { } minimum && value < minimum || field.Maximum is { } maximum && value > maximum)
            throw new InvalidOperationException($"PVar field {field.Path} is outside its declared range.");
    }

}
