using System.Text.Json;
using System.Text.Json.Serialization;

namespace Forge.Host.Domain;

public static class MobyDexSchema
{
    public const int CurrentVersion = 1;
    public const int MaximumJsonBytes = 4_194_304;
    public const int MaximumPVarBytes = 1_048_576;
    public const int MaximumFields = 4_096;
    public const int MaximumOptions = 4_096;
    public const int MaximumArrayCount = 65_536;
    public const int MaximumNestingDepth = 16;
    private const int MaximumJsonDepth = 128;
    internal const int MaximumDiagnostics = 100;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        MaxDepth = MaximumJsonDepth,
    };

    public static MobyDexEntry Parse(ReadOnlyMemory<byte> utf8Json)
    {
        if (!TryParse(utf8Json, out var entry, out var diagnostics))
            throw new MobyDexValidationException(diagnostics);
        return entry!;
    }

    public static bool TryParse(
        ReadOnlyMemory<byte> utf8Json,
        out MobyDexEntry? entry,
        out IReadOnlyList<MobyDexDiagnostic> diagnostics)
    {
        entry = null;
        if (utf8Json.Length is 0 or > MaximumJsonBytes)
        {
            diagnostics = [new("MDEX_JSON_SIZE", "$",
                $"Entry JSON must contain 1 through {MaximumJsonBytes} bytes.")];
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(utf8Json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = MaximumJsonDepth,
            });
            var duplicate = FindDuplicateProperty(document.RootElement, "$", 0);
            if (duplicate is not null)
            {
                diagnostics = [duplicate];
                return false;
            }
            entry = JsonSerializer.Deserialize<MobyDexEntry>(utf8Json.Span, JsonOptions);
            if (entry is null)
            {
                diagnostics = [new("MDEX_JSON_ROOT", "$", "Entry root must be an object.")];
                return false;
            }
        }
        catch (JsonException exception)
        {
            diagnostics = [new("MDEX_JSON_INVALID", exception.Path ?? "$", "Entry is not valid MobyDex v1 JSON.")];
            return false;
        }

        diagnostics = MobyDexValidator.Validate(entry);
        if (diagnostics.Count == 0) return true;
        entry = null;
        return false;
    }

    public static byte[] Format(MobyDexEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var diagnostics = MobyDexValidator.Validate(entry);
        if (diagnostics.Count > 0) throw new MobyDexValidationException(diagnostics);
        return MobyDexFormatter.Format(entry);
    }

    private static MobyDexDiagnostic? FindDuplicateProperty(JsonElement value, string path, int depth)
    {
        if (depth > MaximumJsonDepth)
            return new("MDEX_COMPLEXITY", path, "JSON nesting exceeds the supported limit.");
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                var propertyPath = $"{path}.{property.Name}";
                if (!names.Add(property.Name))
                    return new("MDEX_DUPLICATE_PROPERTY", propertyPath, "JSON property is repeated.");
                var nested = FindDuplicateProperty(property.Value, propertyPath, depth + 1);
                if (nested is not null) return nested;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                var nested = FindDuplicateProperty(item, $"{path}[{index++}]", depth + 1);
                if (nested is not null) return nested;
            }
        }
        return null;
    }
}
