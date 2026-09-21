using System.Text.Json;

namespace Forge.Host.Domain;

public static class ProjectSchema
{
    public const int CurrentVersion = 1;
    public const int CurrentBaseEntityVersion = 1;
    public const string ManifestDocumentType = "forge-project";
    public const string ContentDocumentType = "forge-project-content";

    public static JsonDocument Parse(ReadOnlyMemory<byte> utf8Json)
    {
        var document = JsonDocument.Parse(utf8Json, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64,
        });

        try
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("Project root must be an object");
            }

            JsonElement property = default;
            var matches = 0;
            foreach (var candidate in root.EnumerateObject())
            {
                if (!candidate.NameEquals("schemaVersion")) continue;
                property = candidate.Value;
                matches++;
            }
            if (matches != 1 || !property.TryGetInt32(out var version) || version < 0)
            {
                throw new JsonException("Project must contain exactly one non-negative integer schemaVersion");
            }
            if (version > CurrentVersion) throw new UnsupportedProjectSchemaException(version);
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }
}

public sealed class UnsupportedProjectSchemaException(int foundVersion) : Exception(
    $"Project schema {foundVersion} is unsupported; this Forge build supports schema {ProjectSchema.CurrentVersion}")
{
    public int FoundVersion { get; } = foundVersion;
    public int SupportedVersion { get; } = ProjectSchema.CurrentVersion;
    public bool IsNewer => FoundVersion > SupportedVersion;
}
