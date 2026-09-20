using System.Text.Json;
using System.Text.Json.Serialization;

namespace Forge.Host.Domain;

[JsonConverter(typeof(EntityIdJsonConverter))]
public readonly record struct EntityId
{
    public Guid Value { get; }

    public EntityId(Guid value)
    {
        if (value == Guid.Empty) throw new ArgumentException("Entity ID cannot be empty", nameof(value));
        Value = value;
    }

    public static EntityId New() => new(Guid.NewGuid());

    public static EntityId Parse(string value)
    {
        if (!Guid.TryParseExact(value, "D", out var parsed) || parsed.ToString("D") != value)
        {
            throw new FormatException("Entity ID must use lowercase canonical UUID format");
        }
        return new(parsed);
    }

    public EntityId Duplicate() => New();

    public override string ToString() => Value.ToString("D");
}

public sealed class EntityIdJsonConverter : JsonConverter<EntityId>
{
    public override EntityId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("Entity ID must be a UUID string");
        try
        {
            return EntityId.Parse(reader.GetString()!);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            throw new JsonException("Entity ID must be a non-empty canonical UUID", exception);
        }
    }

    public override void Write(Utf8JsonWriter writer, EntityId value, JsonSerializerOptions options)
    {
        if (value.Value == Guid.Empty) throw new JsonException("Entity ID cannot be empty");
        writer.WriteStringValue(value.ToString());
    }
}
