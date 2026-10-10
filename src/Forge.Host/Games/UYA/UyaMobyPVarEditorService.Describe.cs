using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Forge.Host.Domain;

namespace Forge.Host.Games.UYA;

internal static partial class UyaMobyPVarEditorService
{
    public static EditorMobyPVarDescriptor? Describe(
        ForgeProjectWorkspace workspace,
        MobyDexCatalog catalog,
        ProjectEntity entity,
        EditorMobyPVarResolutionIndex lookup,
        bool includeRawData = false)
    {
        if (entity.Layer != "mobys" || entity.Source is null) return null;
        var resolved = workspace.ResolveMobyDexEntry(catalog, "UYA", entity.Source.ClassId);
        lookup.PVars.TryGetValue(entity.EntityId, out var pvar);
        if (resolved?.Entry.PVar is not { } definition)
            return pvar is null ? null : new(
                "none", 1, 1, "No active schema", string.Empty, Fingerprint(pvar), pvar.Data.Length, true, false,
                "This PVar has no valid MobyDex schema. Its bytes are preserved but cannot be edited.",
                includeRawData ? pvar.Data : null,
                includeRawData ? ModifiedByteMask(pvar) : null,
                [Unknown("unknown", "Unknown bytes", 0, pvar.Data)]);
        var source = resolved.Source == MobyDexEntrySource.Project
            ? "Project override"
            : $"Built-in dataset {resolved.DatasetId} v{resolved.DatasetVersion}";
        if (pvar is null)
            return new(
                resolved.DatasetId, resolved.DatasetVersion, resolved.SchemaVersion, source,
                resolved.Fingerprint, string.Empty,
                definition.Length, false, definition.DefaultHex is not null,
                definition.DefaultHex is null
                    ? "No PVar data is attached and this schema has no verified default."
                    : "No PVar data is attached. Initialize it from the verified schema default to edit fields.",
                null,
                null,
                []);
        if (pvar.Data.Length != definition.Length)
            return new(
                resolved.DatasetId, resolved.DatasetVersion, resolved.SchemaVersion, source,
                resolved.Fingerprint, Fingerprint(pvar),
                pvar.Data.Length, true, false,
                $"The PVar is {pvar.Data.Length} bytes but the active schema expects {definition.Length}; fields are read-only.",
                includeRawData ? pvar.Data : null,
                includeRawData ? ModifiedByteMask(pvar) : null,
                [Unknown("unknown", "Schema length mismatch", 0, pvar.Data)]);
        return new(
            resolved.DatasetId, resolved.DatasetVersion, resolved.SchemaVersion, source,
            resolved.Fingerprint, Fingerprint(pvar),
            pvar.Data.Length, true, false, null,
            includeRawData ? pvar.Data : null,
            includeRawData ? ModifiedByteMask(pvar) : null,
            DescribeFields(definition.Fields, 0, string.Empty, pvar, lookup, definition.Length));
    }

    private static IReadOnlyList<EditorMobyPVarFieldDescriptor> DescribeFields(
        IReadOnlyList<MobyDexField> fields,
        int baseOffset,
        string prefix,
        ProjectMobyPVar pvar,
        EditorMobyPVarResolutionIndex lookup,
        int length)
    {
        var result = fields.Select(field => DescribeField(field, baseOffset, prefix, pvar, lookup)).ToList();
        result.AddRange(Gaps(fields.Select(field => (field.Offset, field.Length)), baseOffset, prefix, pvar.Data, length));
        return result.OrderBy(value => value.Offset).ThenBy(value => value.Path, StringComparer.Ordinal).ToArray();
    }

    private static EditorMobyPVarFieldDescriptor DescribeField(
        MobyDexField field,
        int baseOffset,
        string prefix,
        ProjectMobyPVar pvar,
        EditorMobyPVarResolutionIndex lookup)
    {
        var path = string.IsNullOrEmpty(prefix) ? field.Key : $"{prefix}.{field.Key}";
        return DescribeDefinition(
            field.Definition!, path, field.Label, baseOffset + field.Offset, field.Length,
            field.Help, field.Minimum, field.Maximum, pvar, lookup);
    }

    private static EditorMobyPVarFieldDescriptor DescribeDefinition(
        MobyDexTypeDefinition definition,
        string path,
        string label,
        int offset,
        int length,
        string? help,
        decimal? minimum,
        decimal? maximum,
        ProjectMobyPVar pvar,
        EditorMobyPVarResolutionIndex lookup)
    {
        if (definition.Kind is "struct" or "union")
            return new(path, label, offset, length, EditorMobyPVarFieldKind.Group, null, false, false, help,
                Children: DescribeFields(definition.Fields!, offset, path, pvar, lookup, length));
        if (definition.Kind == "array")
        {
            var children = Enumerable.Range(0, definition.Count!.Value).Select(index => DescribeDefinition(
                definition.Element!, $"{path}[{index}]", $"[{index}]", offset + index * definition.Stride!.Value,
                definition.Stride.Value, help, minimum, maximum, pvar, lookup)).ToArray();
            return new(path, label, offset, length, EditorMobyPVarFieldKind.Group, null, false, false, help,
                Children: children);
        }
        return DescribeLeaf(new(path, label, offset, length, definition, help, minimum, maximum), pvar, lookup);
    }

    private static EditorMobyPVarFieldDescriptor DescribeLeaf(
        Leaf field,
        ProjectMobyPVar pvar,
        EditorMobyPVarResolutionIndex lookup)
    {
        var span = pvar.Data.AsSpan(field.Offset, field.Length);
        var (kind, value, editable, invalid) = ReadValue(field, span);
        EditorMobyPVarReference? reference = null;
        if (field.Definition.Kind == "reference")
        {
            var targetKind = UyaMobyPVarService.TargetKind(field.Definition.TargetKind!);
            var sourceValue = BinaryPrimitives.ReadInt32LittleEndian(span);
            var stored = pvar.References.SingleOrDefault(value => value.Reference.FieldKey == field.Path);
            var target = stored?.Reference.EntityId;
            if (target is null && sourceValue != field.Definition.NullValue)
                lookup.EntitiesBySourceIndex.TryGetValue((targetKind, sourceValue), out target);
            reference = new(targetKind, target, true, sourceValue,
                sourceValue != field.Definition.NullValue && target is null);
            value = new(EditorMobyPVarValueKind.Reference, Reference: target);
            invalid = reference.Missing;
        }
        var options = field.Definition.Options?.Select(option => new EditorMobyPVarOption(
            option.Key, option.Label, option.Value.ToString(CultureInfo.InvariantCulture))).ToArray();
        return new(
            field.Path, field.Label, field.Offset, field.Length, kind, value, editable, invalid, field.Help,
            field.Minimum?.ToString(CultureInfo.InvariantCulture),
            field.Maximum?.ToString(CultureInfo.InvariantCulture), options, reference);
    }

    private static (EditorMobyPVarFieldKind Kind, EditorMobyPVarValue Value, bool Editable, bool Invalid) ReadValue(
        Leaf field,
        ReadOnlySpan<byte> data)
    {
        switch (field.Definition.Kind)
        {
            case "int32": return Integer(BinaryPrimitives.ReadInt32LittleEndian(data), field);
            case "uint8": return Integer(data[0], field);
            case "uint64": return Integer(BinaryPrimitives.ReadUInt64LittleEndian(data), field);
            case "enum":
            case "flags":
                var stored = ReadStoredInteger(field.Definition.Storage!, data);
                var invalid = field.Definition.Kind == "enum"
                    && field.Definition.Options?.Any(option => option.Value == stored) != true;
                return (field.Definition.Kind == "enum" ? EditorMobyPVarFieldKind.Choice : EditorMobyPVarFieldKind.Flags,
                    new(EditorMobyPVarValueKind.Integer, Integer: stored.ToString(CultureInfo.InvariantCulture)),
                    true, invalid);
            case "float32": return Float(BinaryPrimitives.ReadSingleLittleEndian(data), field);
            case "float64": return Float(BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(data)), field);
            case "bool8": return (EditorMobyPVarFieldKind.Boolean,
                new(EditorMobyPVarValueKind.Boolean, Boolean: data[0] != 0), true, data[0] > 1);
            case "rgb8": return Color(data[..3]);
            case "rgba8": return Color(data[..4]);
            case "vector2f32": return Vector(data, 2);
            case "vector3f32": return Vector(data, 3);
            case "reference": return (EditorMobyPVarFieldKind.Reference,
                new(EditorMobyPVarValueKind.Reference), true, false);
            default: return (EditorMobyPVarFieldKind.Bytes,
                new(EditorMobyPVarValueKind.Bytes, Bytes: PreviewHex(data)), false, false);
        }
    }

    private static (EditorMobyPVarFieldKind, EditorMobyPVarValue, bool, bool) Integer(decimal value, Leaf field) =>
        (EditorMobyPVarFieldKind.Integer,
            new(EditorMobyPVarValueKind.Integer, Integer: value.ToString(CultureInfo.InvariantCulture)), true,
            field.Minimum is { } minimum && value < minimum || field.Maximum is { } maximum && value > maximum);

    private static (EditorMobyPVarFieldKind, EditorMobyPVarValue, bool, bool) Float(double value, Leaf field) =>
        (EditorMobyPVarFieldKind.Float, new(EditorMobyPVarValueKind.Float, Float: value), true,
            !double.IsFinite(value)
            || field.Minimum is { } minimum && value < (double)minimum
            || field.Maximum is { } maximum && value > (double)maximum);

    private static (EditorMobyPVarFieldKind, EditorMobyPVarValue, bool, bool) Color(ReadOnlySpan<byte> data) =>
        (EditorMobyPVarFieldKind.Color, new(EditorMobyPVarValueKind.Color, Color: data.ToArray()), true, false);

    private static (EditorMobyPVarFieldKind, EditorMobyPVarValue, bool, bool) Vector(
        ReadOnlySpan<byte> data,
        int count)
    {
        var values = new double[count];
        for (var index = 0; index < count; index++)
            values[index] = BinaryPrimitives.ReadSingleLittleEndian(data[(index * 4)..]);
        return (EditorMobyPVarFieldKind.Vector, new(EditorMobyPVarValueKind.Vector, Vector: values), true,
            values.Any(value => !double.IsFinite(value)));
    }

    private static long ReadStoredInteger(string storage, ReadOnlySpan<byte> data) => storage switch
    {
        "uint8" => data[0],
        "int32" => BinaryPrimitives.ReadInt32LittleEndian(data),
        "uint32" => BinaryPrimitives.ReadUInt32LittleEndian(data),
        _ => throw new InvalidDataException($"Unsupported MobyDex integer storage {storage}."),
    };

    private static IEnumerable<Leaf> Leaves(IReadOnlyList<MobyDexField> fields, int baseOffset, string prefix)
    {
        foreach (var field in fields)
        {
            var path = string.IsNullOrEmpty(prefix) ? field.Key : $"{prefix}.{field.Key}";
            foreach (var leaf in Leaves(field.Definition!, path, baseOffset + field.Offset, field.Length,
                field.Label, field.Help, field.Minimum, field.Maximum)) yield return leaf;
        }
    }

    private static IEnumerable<Leaf> Leaves(
        MobyDexTypeDefinition definition,
        string path,
        int offset,
        int length,
        string label,
        string? help,
        decimal? minimum,
        decimal? maximum)
    {
        if (definition.Kind is "struct" or "union")
        {
            foreach (var leaf in Leaves(definition.Fields!, offset, path)) yield return leaf;
            yield break;
        }
        if (definition.Kind == "array")
        {
            for (var index = 0; index < definition.Count; index++)
                foreach (var leaf in Leaves(definition.Element!, $"{path}[{index}]",
                    offset + index * definition.Stride!.Value, definition.Stride.Value, $"[{index}]", help,
                    minimum, maximum)) yield return leaf;
            yield break;
        }
        yield return new(path, label, offset, length, definition, help, minimum, maximum);
    }

    private static IEnumerable<EditorMobyPVarFieldDescriptor> Gaps(
        IEnumerable<(int Offset, int Length)> fields,
        int baseOffset,
        string prefix,
        byte[] data,
        int length)
    {
        var cursor = 0;
        foreach (var range in fields.OrderBy(value => value.Offset))
        {
            if (range.Offset > cursor)
                yield return Unknown($"{prefix}#unknown-{cursor}", "Unknown bytes", baseOffset + cursor,
                    data.AsSpan(baseOffset + cursor, range.Offset - cursor));
            cursor = Math.Max(cursor, range.Offset + range.Length);
        }
        if (cursor < length)
            yield return Unknown($"{prefix}#unknown-{cursor}", "Unknown bytes", baseOffset + cursor,
                data.AsSpan(baseOffset + cursor, length - cursor));
    }

    private static EditorMobyPVarFieldDescriptor Unknown(
        string path,
        string label,
        int offset,
        ReadOnlySpan<byte> bytes) => new(
            path, label, offset, bytes.Length, EditorMobyPVarFieldKind.Unknown,
            new(EditorMobyPVarValueKind.Bytes, Bytes: PreviewHex(bytes)), false, false);

    private static string PreviewHex(ReadOnlySpan<byte> bytes)
    {
        const int maximumBytes = 256;
        var preview = Convert.ToHexString(bytes[..Math.Min(bytes.Length, maximumBytes)]).ToLowerInvariant();
        return bytes.Length <= maximumBytes ? preview : $"{preview}… ({bytes.Length - maximumBytes} more bytes)";
    }

    private static string Fingerprint(ProjectMobyPVar pvar)
    {
        var referenceState = string.Join('|', pvar.References.OrderBy(value => value.Reference.FieldKey, StringComparer.Ordinal)
            .Select(value => $"{value.Offset}:{value.Length}:{value.NullValue}:{value.Reference.Domain}:"
                + $"{value.Reference.FieldKey}:{value.Reference.EntityKind}:{value.SourceValue}:"
                + value.Reference.EntityId));
        var suffix = Encoding.UTF8.GetBytes(referenceState);
        var input = new byte[pvar.Data.Length + suffix.Length];
        pvar.Data.CopyTo(input, 0);
        suffix.CopyTo(input, pvar.Data.Length);
        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }

    private static byte[]? ModifiedByteMask(ProjectMobyPVar pvar)
    {
        var mask = new byte[(pvar.Data.Length + 7) / 8];
        var modified = false;
        for (var offset = 0; offset < pvar.Data.Length; offset++)
        {
            if (pvar.Data[offset] == pvar.BaselineData[offset]) continue;
            mask[offset / 8] |= (byte)(1 << (offset % 8));
            modified = true;
        }
        return modified ? mask : null;
    }

    private sealed record Leaf(
        string Path,
        string Label,
        int Offset,
        int Length,
        MobyDexTypeDefinition Definition,
        string? Help,
        decimal? Minimum,
        decimal? Maximum);
}
