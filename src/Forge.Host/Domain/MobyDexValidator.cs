namespace Forge.Host.Domain;

internal static class MobyDexValidator
{
    private static readonly IReadOnlyDictionary<string, int> FixedSizes = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["int8"] = 1, ["uint8"] = 1, ["int16"] = 2, ["uint16"] = 2,
        ["int32"] = 4, ["uint32"] = 4, ["int64"] = 8, ["uint64"] = 8,
        ["float32"] = 4, ["float64"] = 8, ["bool8"] = 1, ["bool32"] = 4,
        ["rgb8"] = 3, ["rgba8"] = 4,
        ["vector2f32"] = 8, ["vector3f32"] = 12, ["vector4f32"] = 16,
    };
    private static readonly IReadOnlyDictionary<string, (long Minimum, ulong Maximum)> IntegerRanges =
        new Dictionary<string, (long, ulong)>(StringComparer.Ordinal)
        {
            ["int8"] = (sbyte.MinValue, (ulong)sbyte.MaxValue),
            ["uint8"] = (0, byte.MaxValue),
            ["int16"] = (short.MinValue, (ulong)short.MaxValue),
            ["uint16"] = (0, ushort.MaxValue),
            ["int32"] = (int.MinValue, int.MaxValue),
            ["uint32"] = (0, uint.MaxValue),
            ["int64"] = (long.MinValue, long.MaxValue),
            ["uint64"] = (0, ulong.MaxValue),
        };
    private static readonly HashSet<string> EnumStorages =
        ["int8", "uint8", "int16", "uint16", "int32", "uint32"];
    private static readonly HashSet<string> EntityTargetKinds =
    [
        "entity", "moby", "tie", "shrub", "tfrag", "cuboid", "sphere", "cylinder", "pill", "spline",
        "grindPath", "area", "collision", "skyShell", "directionalLight", "pointLight", "environmentSample",
        "environmentTransition", "camera", "ambientSound",
    ];
    private static readonly HashSet<string> AssetTargetKinds =
    [
        "texture", "material", "tie", "shrub", "tfrag", "moby", "animation", "sound", "sky", "gameplay",
        "world", "collision", "lighting",
    ];

    public static IReadOnlyList<MobyDexDiagnostic> Validate(MobyDexEntry entry)
    {
        var state = new ValidationState();
        if (entry.SchemaVersion != MobyDexSchema.CurrentVersion)
            state.Add("MDEX_SCHEMA_VERSION", "$.schemaVersion",
                $"Schema version must be {MobyDexSchema.CurrentVersion}.");
        if (!IsGameId(entry.Game))
            state.Add("MDEX_GAME", "$.game", "Game must be an uppercase game ID of 2 through 16 characters.");
        if (entry.OClass is < 0 or > ushort.MaxValue)
            state.Add("MDEX_OCLASS", "$.oClass", "OClass must be between 0 and 65535.");
        ValidateText(state, entry.Name, "$.name", "name", 128, required: true);
        ValidateText(state, entry.Description, "$.description", "description", 2_048, required: false);
        if (entry.Tags is null) state.Add("MDEX_TAGS", "$.tags", "Tags cannot be null.");
        var entryTags = entry.Tags ?? [];
        if (entryTags.Count > 64)
            state.Add("MDEX_TAG_COUNT", "$.tags", "An entry can contain at most 64 tags.");
        var tags = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < entryTags.Count; index++)
        {
            var path = $"$.tags[{index}]";
            ValidateText(state, entryTags[index], path, "tag", 64, required: true);
            if (entryTags[index] is { } tag && !tags.Add(tag))
                state.Add("MDEX_DUPLICATE_TAG", path, "Tag is repeated.");
        }
        if (entry.PVar is null)
        {
            state.Add("MDEX_PVAR_REQUIRED", "$.pvar", "PVar definition is required.");
            return state.Diagnostics;
        }
        ValidatePVar(state, entry.PVar);
        return state.Diagnostics;
    }

    private static void ValidatePVar(ValidationState state, MobyDexPVar pvar)
    {
        if (pvar.Length is < 0 or > MobyDexSchema.MaximumPVarBytes)
            state.Add("MDEX_PVAR_LENGTH", "$.pvar.length",
                $"PVar length must be between 0 and {MobyDexSchema.MaximumPVarBytes} bytes.");
        if (pvar.DefaultHex is { } defaultHex
            && (pvar.Length < 0 || defaultHex.Length != (long)pvar.Length * 2 || !IsHex(defaultHex)))
            state.Add("MDEX_DEFAULT", "$.pvar.defaultHex",
                "Default must be hexadecimal data exactly matching the declared PVar length.");
        if (pvar.Relocations is null)
            state.Add("MDEX_RELOCATIONS", "$.pvar.relocations", "Relocations cannot be null.");
        var relocationsList = pvar.Relocations ?? [];
        if (relocationsList.Count > MobyDexSchema.MaximumFields)
            state.Add("MDEX_RELOCATION_COUNT", "$.pvar.relocations",
                $"A PVar can contain at most {MobyDexSchema.MaximumFields} relocations.");
        var relocations = new HashSet<int>();
        for (var index = 0; index < relocationsList.Count; index++)
        {
            var offset = relocationsList[index];
            var path = $"$.pvar.relocations[{index}]";
            if (offset < 0 || offset % 4 != 0 || (long)offset + 4 > pvar.Length)
                state.Add("MDEX_RELOCATION", path,
                    "Relocation must be four-byte aligned and address four bytes inside the PVar.");
            if (!relocations.Add(offset)) state.Add("MDEX_DUPLICATE_RELOCATION", path, "Relocation is repeated.");
        }
        ValidateFields(state, pvar.Fields, pvar.Length, "$.pvar.fields", allowOverlap: false, depth: 0);
    }

    private static void ValidateFields(
        ValidationState state,
        IReadOnlyList<MobyDexField>? fields,
        int containerLength,
        string path,
        bool allowOverlap,
        int depth)
    {
        if (fields is null)
        {
            state.Add("MDEX_FIELDS_REQUIRED", path, "Field list cannot be null.");
            return;
        }
        if (depth > MobyDexSchema.MaximumNestingDepth)
        {
            state.Add("MDEX_COMPLEXITY", path, "Field nesting exceeds the supported limit.");
            return;
        }
        state.FieldCount += fields.Count;
        if (state.FieldCount > MobyDexSchema.MaximumFields)
            state.Add("MDEX_FIELD_COUNT", path, $"An entry can contain at most {MobyDexSchema.MaximumFields} fields.");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var spans = new List<(long Start, long End, string Key)>();
        for (var index = 0; index < fields.Count && !state.IsFull; index++)
        {
            var field = fields[index];
            var fieldPath = $"{path}[{index}]";
            if (field is null)
            {
                state.Add("MDEX_FIELD_REQUIRED", fieldPath, "Field cannot be null.");
                continue;
            }
            if (!IsKey(field.Key)) state.Add("MDEX_FIELD_KEY", $"{fieldPath}.key", "Field key is not canonical.");
            if (!keys.Add(field.Key)) state.Add("MDEX_DUPLICATE_KEY", $"{fieldPath}.key", "Sibling field key is repeated.");
            ValidateText(state, field.Label, $"{fieldPath}.label", "field label", 128, required: true);
            ValidateText(state, field.Help, $"{fieldPath}.help", "field help", 1_024, required: false);
            var end = (long)field.Offset + field.Length;
            if (field.Offset < 0 || field.Length <= 0 || end > containerLength)
                state.Add("MDEX_FIELD_SPAN", fieldPath,
                    "Field offset and length must describe a non-empty span inside its container.");
            else if (!allowOverlap)
            {
                var overlap = spans.FirstOrDefault(value => field.Offset < value.End && end > value.Start);
                if (overlap.Key is not null)
                    state.Add("MDEX_FIELD_OVERLAP", $"{fieldPath}.offset",
                        $"Field overlaps sibling '{overlap.Key}'; use an explicit union for shared bytes.");
                spans.Add((field.Offset, end, field.Key));
            }
            if (field.Minimum is { } minimum && field.Maximum is { } maximum && minimum > maximum)
                state.Add("MDEX_BOUNDS", fieldPath, "Minimum cannot exceed maximum.");
            if (field.Definition is null)
            {
                state.Add("MDEX_TYPE_REQUIRED", $"{fieldPath}.definition", "Field type definition is required.");
                continue;
            }
            ValidateDefinition(state, field.Definition, field.Length,
                $"{fieldPath}.definition", $"{fieldPath}.length", fieldPath, depth + 1, exactLength: true,
                field.Minimum, field.Maximum);
        }
    }

    private static void ValidateDefinition(
        ValidationState state,
        MobyDexTypeDefinition definition,
        int availableLength,
        string path,
        string spanPath,
        string boundsPath,
        int depth,
        bool exactLength,
        decimal? minimum = null,
        decimal? maximum = null)
    {
        if (depth > MobyDexSchema.MaximumNestingDepth)
        {
            state.Add("MDEX_COMPLEXITY", path, "Field nesting exceeds the supported limit.");
            return;
        }
        var kind = definition.Kind ?? string.Empty;
        if (FixedSizes.TryGetValue(kind, out var fixedSize))
        {
            RejectUnexpected(state, definition, path);
            ValidateSize(state, spanPath, fixedSize, availableLength, exactLength);
            ValidateBounds(state, kind, minimum, maximum, boundsPath);
            return;
        }
        switch (kind)
        {
            case "bytes":
                RejectUnexpected(state, definition, path);
                RejectBounds(state, minimum, maximum, boundsPath);
                break;
            case "enum":
            case "flags":
                ValidateChoice(state, definition, availableLength, path, spanPath, boundsPath,
                    exactLength, minimum, maximum);
                break;
            case "reference":
                ValidateReference(state, definition, availableLength, path, spanPath, boundsPath,
                    exactLength, minimum, maximum);
                break;
            case "struct":
            case "union":
                RejectUnexpected(state, definition, path, "fields");
                RejectBounds(state, minimum, maximum, boundsPath);
                if (definition.Fields is null || definition.Fields.Count == 0)
                    state.Add("MDEX_COMPOSITE_FIELDS", $"{path}.fields", "Composite type requires fields.");
                else if (kind == "union" && definition.Fields.Count < 2)
                    state.Add("MDEX_UNION_FIELDS", $"{path}.fields", "Union requires at least two alternatives.");
                else
                    ValidateFields(state, definition.Fields, availableLength, $"{path}.fields",
                        allowOverlap: kind == "union", depth);
                break;
            case "array":
                ValidateArray(state, definition, availableLength, path, spanPath, boundsPath,
                    depth, exactLength, minimum, maximum);
                break;
            default:
                state.Add("MDEX_TYPE_UNKNOWN", $"{path}.kind", $"Unknown field type '{kind}'.");
                break;
        }
    }

    private static void ValidateChoice(
        ValidationState state,
        MobyDexTypeDefinition definition,
        int availableLength,
        string path,
        string spanPath,
        string boundsPath,
        bool exactLength,
        decimal? minimum,
        decimal? maximum)
    {
        RejectUnexpected(state, definition, path, "storage", "options");
        if (definition.Storage is null || !EnumStorages.Contains(definition.Storage))
        {
            state.Add("MDEX_STORAGE", $"{path}.storage", "Enum and flags storage must be a scalar integer up to 32 bits.");
            return;
        }
        ValidateSize(state, spanPath, FixedSizes[definition.Storage], availableLength, exactLength);
        ValidateBounds(state, definition.Storage, minimum, maximum, boundsPath);
        if (definition.Options is null || definition.Options.Count == 0)
        {
            state.Add("MDEX_OPTIONS_REQUIRED", $"{path}.options", "Enum and flags types require options.");
            return;
        }
        state.OptionCount += definition.Options.Count;
        if (state.OptionCount > MobyDexSchema.MaximumOptions)
            state.Add("MDEX_OPTION_COUNT", $"{path}.options",
                $"An entry can contain at most {MobyDexSchema.MaximumOptions} options.");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var values = new HashSet<long>();
        for (var index = 0; index < definition.Options.Count && !state.IsFull; index++)
        {
            var option = definition.Options[index];
            var optionPath = $"{path}.options[{index}]";
            if (option is null)
            {
                state.Add("MDEX_OPTION_REQUIRED", optionPath, "Option cannot be null.");
                continue;
            }
            if (!IsKey(option.Key)) state.Add("MDEX_OPTION_KEY", $"{optionPath}.key", "Option key is not canonical.");
            if (!keys.Add(option.Key)) state.Add("MDEX_DUPLICATE_OPTION", $"{optionPath}.key", "Option key is repeated.");
            if (!values.Add(option.Value)) state.Add("MDEX_DUPLICATE_OPTION", $"{optionPath}.value", "Option value is repeated.");
            ValidateText(state, option.Label, $"{optionPath}.label", "option label", 128, required: true);
            if (!FitsInteger(option.Value, definition.Storage))
                state.Add("MDEX_OPTION_RANGE", $"{optionPath}.value", "Option value does not fit its storage type.");
            if (definition.Kind == "flags" && (option.Value <= 0 || (option.Value & (option.Value - 1)) != 0))
                state.Add("MDEX_FLAG_VALUE", $"{optionPath}.value", "Flag option must contain one positive bit.");
        }
    }

    private static void ValidateReference(
        ValidationState state,
        MobyDexTypeDefinition definition,
        int availableLength,
        string path,
        string spanPath,
        string boundsPath,
        bool exactLength,
        decimal? minimum,
        decimal? maximum)
    {
        RejectUnexpected(state, definition, path, "storage", "domain", "targetKind", "nullValue");
        RejectBounds(state, minimum, maximum, boundsPath);
        if (definition.Storage is null || !EnumStorages.Contains(definition.Storage))
            state.Add("MDEX_STORAGE", $"{path}.storage", "Reference storage must be a scalar integer up to 32 bits.");
        else
        {
            ValidateSize(state, spanPath, FixedSizes[definition.Storage], availableLength, exactLength);
            if (definition.NullValue is { } nullValue && !FitsInteger(nullValue, definition.Storage))
                state.Add("MDEX_REFERENCE_NULL", $"{path}.nullValue", "Null sentinel does not fit reference storage.");
        }
        if (definition.NullValue is null)
            state.Add("MDEX_REFERENCE_NULL", $"{path}.nullValue", "Reference requires an explicit null sentinel.");
        var targets = definition.Domain switch
        {
            "entity" => EntityTargetKinds,
            "asset" => AssetTargetKinds,
            _ => null,
        };
        if (targets is null) state.Add("MDEX_REFERENCE_DOMAIN", $"{path}.domain", "Reference domain must be entity or asset.");
        else if (definition.TargetKind is null || !targets.Contains(definition.TargetKind))
            state.Add("MDEX_REFERENCE_KIND", $"{path}.targetKind",
                "Reference target kind is not valid for its domain.");
    }

    private static void ValidateArray(
        ValidationState state,
        MobyDexTypeDefinition definition,
        int availableLength,
        string path,
        string spanPath,
        string boundsPath,
        int depth,
        bool exactLength,
        decimal? minimum,
        decimal? maximum)
    {
        RejectUnexpected(state, definition, path, "count", "stride", "element");
        RejectBounds(state, minimum, maximum, boundsPath);
        if (definition.Count is null or <= 0 or > MobyDexSchema.MaximumArrayCount)
            state.Add("MDEX_ARRAY_COUNT", $"{path}.count",
                $"Array count must be between 1 and {MobyDexSchema.MaximumArrayCount}.");
        if (definition.Stride is null or <= 0)
            state.Add("MDEX_ARRAY_STRIDE", $"{path}.stride", "Array stride must be positive.");
        if (definition.Count is { } count && definition.Stride is { } stride && count > 0 && stride > 0)
        {
            var required = (long)count * stride;
            if (required > int.MaxValue || required > availableLength || exactLength && required != availableLength)
                state.Add("MDEX_ARRAY_SPAN", spanPath, "Array count and stride must exactly fit the field span.");
        }
        if (definition.Element is null)
            state.Add("MDEX_ARRAY_ELEMENT", $"{path}.element", "Array element type is required.");
        else if (definition.Stride is > 0)
            ValidateDefinition(state, definition.Element, definition.Stride.Value,
                $"{path}.element", $"{path}.element", $"{path}.element", depth + 1, exactLength: false);
    }

    private static void ValidateSize(
        ValidationState state,
        string spanPath,
        int required,
        int available,
        bool exact)
    {
        if (required > available || exact && required != available)
            state.Add("MDEX_TYPE_LENGTH", spanPath,
                $"Field length must be {(exact ? "exactly " : "at least ")}{required} bytes for its type.");
    }

    private static void ValidateBounds(
        ValidationState state,
        string kind,
        decimal? minimum,
        decimal? maximum,
        string fieldPath)
    {
        if (minimum is null && maximum is null) return;
        if (kind is "float32" or "float64") return;
        if (!IntegerRanges.TryGetValue(kind, out var range))
        {
            RejectBounds(state, minimum, maximum, fieldPath);
            return;
        }
        if (minimum is { } min
            && (min != decimal.Truncate(min) || min < range.Minimum || min > (decimal)range.Maximum))
            state.Add("MDEX_BOUNDS", $"{fieldPath}.minimum", "Minimum does not fit the field type.");
        if (maximum is { } max
            && (max != decimal.Truncate(max) || max < range.Minimum || max > (decimal)range.Maximum))
            state.Add("MDEX_BOUNDS", $"{fieldPath}.maximum", "Maximum does not fit the field type.");
    }

    private static void RejectBounds(
        ValidationState state,
        decimal? minimum,
        decimal? maximum,
        string fieldPath)
    {
        if (minimum is not null || maximum is not null)
            state.Add("MDEX_BOUNDS_TYPE", fieldPath, "Bounds are only valid on numeric and enum fields.");
    }

    private static void RejectUnexpected(
        ValidationState state,
        MobyDexTypeDefinition definition,
        string path,
        params string[] allowed)
    {
        var set = allowed.ToHashSet(StringComparer.Ordinal);
        if (definition.Storage is not null && !set.Contains("storage")) Add("storage");
        if (definition.Options is not null && !set.Contains("options")) Add("options");
        if (definition.Count is not null && !set.Contains("count")) Add("count");
        if (definition.Stride is not null && !set.Contains("stride")) Add("stride");
        if (definition.Element is not null && !set.Contains("element")) Add("element");
        if (definition.Fields is not null && !set.Contains("fields")) Add("fields");
        if (definition.Domain is not null && !set.Contains("domain")) Add("domain");
        if (definition.TargetKind is not null && !set.Contains("targetKind")) Add("targetKind");
        if (definition.NullValue is not null && !set.Contains("nullValue")) Add("nullValue");
        void Add(string property) => state.Add("MDEX_TYPE_PROPERTY", $"{path}.{property}",
            $"Property is not valid for type '{definition.Kind}'.");
    }

    private static bool FitsInteger(long value, string storage)
    {
        if (!IntegerRanges.TryGetValue(storage, out var range) || value < range.Minimum) return false;
        return value >= 0 ? (ulong)value <= range.Maximum : range.Minimum < 0;
    }

    private static void ValidateText(
        ValidationState state,
        string? value,
        string path,
        string label,
        int maximum,
        bool required)
    {
        if (value is null || required && string.IsNullOrWhiteSpace(value))
        {
            if (required) state.Add("MDEX_TEXT_REQUIRED", path, $"{label} is required.");
            return;
        }
        if (value.Length > maximum || value.Any(char.IsControl))
            state.Add("MDEX_TEXT", path, $"{label} must contain at most {maximum} characters and no controls.");
    }

    private static bool IsGameId(string? value) => value is not null && value.Length is >= 2 and <= 16
        && value[0] is >= 'A' and <= 'Z'
        && value.All(character => character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '-');

    private static bool IsKey(string? value) => value is not null && value.Length is >= 1 and <= 64
        && value[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z'
        && value.All(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z'
            or >= '0' and <= '9' or '_' or '-');

    private static bool IsHex(string value) => value.All(character =>
        character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private sealed class ValidationState
    {
        private readonly List<MobyDexDiagnostic> _diagnostics = [];
        public IReadOnlyList<MobyDexDiagnostic> Diagnostics => _diagnostics;
        public int FieldCount { get; set; }
        public int OptionCount { get; set; }
        public bool IsFull => _diagnostics.Count >= MobyDexSchema.MaximumDiagnostics;

        public void Add(string code, string path, string message)
        {
            if (!IsFull) _diagnostics.Add(new(code, path, message));
        }
    }
}
