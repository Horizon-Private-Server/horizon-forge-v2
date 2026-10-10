using System.Globalization;
using System.Text;
using System.Text.Json;
using Forge.Host.Domain;

internal static class MobyDexSchemaTests
{
    public static void Run()
    {
        using (var schema = JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "fixtures", "mobydex-v1.schema.json"))))
        {
            Equal("https://json-schema.org/draft/2020-12/schema",
                schema.RootElement.GetProperty("$schema").GetString(), "machine-readable schema dialect");
            Equal("Horizon Forge MobyDex entry v1",
                schema.RootElement.GetProperty("title").GetString(), "machine-readable schema title");
        }
        var fixture = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "mobydex-v1.json"));
        var entry = MobyDexSchema.Parse(fixture);
        Equal("TEST", entry.Game, "fixture game");
        Equal(1, entry.OClass, "fixture OClass");
        Equal(18, entry.PVar!.Fields.Count, "fixture field count");
        var kinds = AllKinds(entry.PVar.Fields).ToHashSet(StringComparer.Ordinal);
        foreach (var kind in new[]
        {
            "float32", "uint8", "enum", "flags", "bool8", "rgb8", "rgba8", "vector3f32", "bytes",
            "array", "int32", "reference", "struct", "union", "vector2f32", "float64", "uint64",
        }) Equal(true, kinds.Contains(kind), $"fixture kind {kind}");
        Equal(2, entry.PVar.Fields.Count(value => value.Definition?.Kind == "reference"),
            "legacy MobyRef and CuboidRef become typed references");

        var canonical = MobyDexSchema.Format(entry);
        Equal(Encoding.UTF8.GetString(fixture), Encoding.UTF8.GetString(canonical), "canonical fixture snapshot");
        Equal(false, canonical.Contains((byte)'\r'), "canonical output uses platform-independent newlines");
        Equal((byte)'\n', canonical[^1], "canonical output has one final newline");
        Equal(Encoding.UTF8.GetString(canonical), Encoding.UTF8.GetString(MobyDexSchema.Format(
            MobyDexSchema.Parse(canonical))), "canonical parse-format fixed point");

        var entryJson = Encoding.UTF8.GetString(canonical).TrimEnd('\n');
        var dataset = MobyDexDatasetSchema.Parse(Encoding.UTF8.GetBytes(
            $$"""{"id":"test.dataset","version":1,"entries":[{{entryJson}}]}"""));
        Equal("test.dataset", dataset.Id, "dataset ID");
        Equal(1, dataset.Version, "dataset version");
        Equal(1, dataset.Entries.Count, "dataset entry count");
        RejectDatasetJson("""{"id":"x","id":"y","version":1,"entries":[]}"""u8.ToArray(),
            "duplicate dataset property");
        RejectDatasetJson("""{"id":"x","version":1,"entries":[],"unknown":true}"""u8.ToArray(),
            "unknown dataset property");
        RejectDatasetJson(Encoding.UTF8.GetBytes(
            $$"""{"id":"x","version":1,"entries":[{{entryJson}},{{entryJson}}]}"""),
            "duplicate dataset entry");

        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Equal(Encoding.UTF8.GetString(canonical), Encoding.UTF8.GetString(MobyDexSchema.Format(entry)),
                "canonical formatting ignores process culture");
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }

        RejectJson([], "MDEX_JSON_SIZE", "empty JSON");
        RejectJson(new byte[MobyDexSchema.MaximumJsonBytes + 1], "MDEX_JSON_SIZE", "oversized JSON");
        RejectJson("{\"schemaVersion\":1,\"schemaVersion\":1}"u8.ToArray(),
            "MDEX_DUPLICATE_PROPERTY", "duplicate JSON property");
        RejectJson("{\"schemaVersion\":1,\"unknown\":true}"u8.ToArray(),
            "MDEX_JSON_INVALID", "unknown JSON property");
        RejectJson("{\"schemaVersion\":1,\"game\":\"UYA\",\"name\":\"Missing OClass\",\"pvar\":{\"length\":0,\"fields\":[]}}"u8.ToArray(),
            "MDEX_JSON_INVALID", "missing required numeric property");
        RejectJson("{\"schemaVersion\":1,\"game\":\"UYA\",\"oClass\":1,\"name\":\"Null fields\",\"pvar\":{\"length\":0,\"fields\":null}}"u8.ToArray(),
            "MDEX_FIELDS_REQUIRED", "null field list");
        RejectJson("{\"schemaVersion\":1,\"game\":\"UYA\",\"oClass\":1,\"name\":\"X\",\"tags\":[],\"pvar\":"u8
            .ToArray(), "MDEX_JSON_INVALID", "truncated JSON");

        Reject(entry with { SchemaVersion = 2 }, "MDEX_SCHEMA_VERSION", "$.schemaVersion");
        Reject(entry with { Game = "uya" }, "MDEX_GAME", "$.game");
        Reject(entry with { OClass = 65_536 }, "MDEX_OCLASS", "$.oClass");
        Reject(entry with { PVar = entry.PVar with { DefaultHex = "00" } }, "MDEX_DEFAULT", "$.pvar.defaultHex");
        Reject(entry with { PVar = entry.PVar with { Relocations = [2] } }, "MDEX_RELOCATION",
            "$.pvar.relocations[0]");

        var primitive = PrimitiveField("value", 0, 4, "int32");
        var minimal = Minimal([primitive]);
        Reject(minimal with { PVar = minimal.PVar! with
        {
            Length = 8, Fields = [primitive, primitive with { Key = "other", Offset = 2 }],
        } }, "MDEX_FIELD_OVERLAP", "$.pvar.fields[1].offset");
        Reject(minimal with { PVar = minimal.PVar! with
        {
            Fields = [primitive, primitive with { Offset = int.MaxValue }],
        } }, "MDEX_FIELD_SPAN", "$.pvar.fields[1]");
        Reject(minimal with { PVar = minimal.PVar! with
        {
            Length = 8, Fields = [primitive, primitive with { Offset = 4 }],
        } }, "MDEX_DUPLICATE_KEY", "$.pvar.fields[1].key");
        Reject(Minimal([primitive with { Definition = new() { Kind = "script" } }]),
            "MDEX_TYPE_UNKNOWN", "$.pvar.fields[0].definition.kind");
        Reject(Minimal([primitive with
        {
            Length = 3,
            Definition = new() { Kind = "array", Count = 2, Stride = 2, Element = new() { Kind = "uint8" } },
        }]), "MDEX_ARRAY_SPAN", "$.pvar.fields[0].length");
        Reject(Minimal([primitive with
        {
            Definition = new()
            {
                Kind = "reference", Storage = "int32", Domain = "entity", TargetKind = "moby",
            },
        }]), "MDEX_REFERENCE_NULL", "$.pvar.fields[0].definition.nullValue");

        var duplicateOptions = new[]
        {
            new MobyDexOption { Key = "same", Label = "First", Value = 1 },
            new MobyDexOption { Key = "same", Label = "Second", Value = 1 },
        };
        Reject(Minimal([primitive with
        {
            Length = 1,
            Definition = new() { Kind = "enum", Storage = "uint8", Options = duplicateOptions },
        }]), "MDEX_DUPLICATE_OPTION", "$.pvar.fields[0].definition.options[1].key");
        Reject(Minimal([primitive with
        {
            Length = 1,
            Definition = new()
            {
                Kind = "flags", Storage = "uint8",
                Options = [new() { Key = "bad", Label = "Bad", Value = 3 }],
            },
        }]), "MDEX_FLAG_VALUE", "$.pvar.fields[0].definition.options[0].value");

        var nested = new MobyDexTypeDefinition { Kind = "int32" };
        for (var depth = 0; depth <= MobyDexSchema.MaximumNestingDepth; depth++)
            nested = new()
            {
                Kind = "struct",
                Fields = [primitive with { Key = $"level{depth}", Definition = nested }],
            };
        Reject(Minimal([primitive with { Definition = nested }]), "MDEX_COMPLEXITY", null);

        var tooManyFields = Enumerable.Range(0, MobyDexSchema.MaximumFields + 1)
            .Select(index => PrimitiveField($"f{index}", index * 4, 4, "int32")).ToArray();
        Reject(new MobyDexEntry
        {
            SchemaVersion = 1, Game = "UYA", OClass = 1, Name = "Too many",
            PVar = new() { Length = tooManyFields.Length * 4, Fields = tooManyFields },
        }, "MDEX_FIELD_COUNT", "$.pvar.fields");

        RejectJson(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(fixture).Replace(
            "\"help\": \"Forge v1 Float.\"", "\"default\": 1", StringComparison.Ordinal)),
            "MDEX_JSON_INVALID", "ambiguous field default is not accepted");

        var random = new Random(0x4d444558);
        for (var iteration = 0; iteration < 256; iteration++)
        {
            var bytes = new byte[random.Next(1, 257)];
            random.NextBytes(bytes);
            _ = MobyDexSchema.TryParse(bytes, out _, out _);
        }
    }

    private static IEnumerable<string> AllKinds(IReadOnlyList<MobyDexField> fields)
    {
        foreach (var field in fields)
        {
            if (field.Definition is null) continue;
            yield return field.Definition.Kind;
            if (field.Definition.Element is { } element)
            {
                yield return element.Kind;
                if (element.Fields is { } elementFields)
                    foreach (var kind in AllKinds(elementFields)) yield return kind;
            }
            if (field.Definition.Fields is { } nested)
                foreach (var kind in AllKinds(nested)) yield return kind;
        }
    }

    private static MobyDexEntry Minimal(IReadOnlyList<MobyDexField> fields) => new()
    {
        SchemaVersion = 1,
        Game = "UYA",
        OClass = 1,
        Name = "Synthetic",
        PVar = new() { Length = fields.Count == 0 ? 0 : fields.Max(value => value.Offset + value.Length), Fields = fields },
    };

    private static MobyDexField PrimitiveField(string key, int offset, int length, string kind) => new()
    {
        Key = key,
        Label = key,
        Offset = offset,
        Length = length,
        Definition = new() { Kind = kind },
    };

    private static void Reject(MobyDexEntry entry, string code, string? path)
    {
        try
        {
            _ = MobyDexSchema.Format(entry);
        }
        catch (MobyDexValidationException exception)
        {
            var diagnostic = exception.Diagnostics.FirstOrDefault(value => value.Code == code
                && (path is null || value.Path == path));
            if (diagnostic is not null) return;
            throw new InvalidOperationException(
                $"Expected {code} at {path ?? "any path"}; got {string.Join(", ", exception.Diagnostics)}");
        }
        throw new InvalidOperationException($"Expected invalid MobyDex entry with {code}.");
    }

    private static void RejectJson(byte[] json, string code, string context)
    {
        if (MobyDexSchema.TryParse(json, out _, out var diagnostics))
            throw new InvalidOperationException($"{context}: expected parser rejection");
        if (diagnostics.All(value => value.Code != code))
            throw new InvalidOperationException($"{context}: expected {code}, got {string.Join(", ", diagnostics)}");
    }

    private static void RejectDatasetJson(byte[] json, string context)
    {
        try
        {
            _ = MobyDexDatasetSchema.Parse(json);
        }
        catch (InvalidDataException)
        {
            return;
        }
        throw new InvalidOperationException($"{context}: expected parser rejection");
    }

    private static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected {expected}, got {actual}");
    }
}
