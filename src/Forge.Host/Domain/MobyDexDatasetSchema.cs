using System.Text;
using System.Text.Json;

namespace Forge.Host.Domain;

public static class MobyDexDatasetSchema
{
    public const int MaximumJsonBytes = 16 * 1024 * 1024;

    public static MobyDexDataset Parse(ReadOnlyMemory<byte> utf8Json)
    {
        if (utf8Json.Length is 0 or > MaximumJsonBytes)
            throw new InvalidDataException(
                $"MobyDex dataset JSON must contain 1 through {MaximumJsonBytes} bytes.");
        try
        {
            using var document = JsonDocument.Parse(utf8Json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 128,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("MobyDex dataset root must be an object.");
            var properties = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!properties.Add(property.Name))
                    throw new InvalidDataException($"MobyDex dataset property {property.Name} is repeated.");
                if (property.Name is not ("id" or "version" or "entries"))
                    throw new InvalidDataException($"MobyDex dataset property {property.Name} is unknown.");
            }
            if (!root.TryGetProperty("id", out var idProperty)
                || idProperty.ValueKind != JsonValueKind.String
                || idProperty.GetString() is not { } id
                || string.IsNullOrWhiteSpace(id) || id.Length > 128)
                throw new InvalidDataException("MobyDex dataset ID is invalid.");
            if (!root.TryGetProperty("version", out var versionProperty)
                || !versionProperty.TryGetInt32(out var version) || version < 1)
                throw new InvalidDataException("MobyDex dataset version is invalid.");
            if (!root.TryGetProperty("entries", out var entriesProperty)
                || entriesProperty.ValueKind != JsonValueKind.Array
                || entriesProperty.GetArrayLength() > ProjectMobyDexSchema.MaximumEntries)
                throw new InvalidDataException("MobyDex dataset entry list is invalid.");

            var entries = new List<MobyDexEntry>(entriesProperty.GetArrayLength());
            var keys = new HashSet<(string Game, int OClass)>();
            foreach (var value in entriesProperty.EnumerateArray())
            {
                MobyDexEntry entry;
                try
                {
                    entry = MobyDexSchema.Parse(Encoding.UTF8.GetBytes(value.GetRawText()));
                }
                catch (MobyDexValidationException exception)
                {
                    throw new InvalidDataException(
                        $"MobyDex dataset entry {entries.Count} is invalid: {exception.Message}", exception);
                }
                if (!keys.Add((entry.Game, entry.OClass)))
                    throw new InvalidDataException(
                        $"MobyDex dataset contains duplicate entry {entry.Game}/{entry.OClass}.");
                entries.Add(entry);
            }
            return new(id, version, entries);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("MobyDex dataset is not valid JSON.", exception);
        }
    }
}
