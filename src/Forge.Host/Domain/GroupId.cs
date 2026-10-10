using System.Text.Json;
using System.Text.Json.Serialization;

namespace Forge.Host.Domain;

[JsonConverter(typeof(GroupIdJsonConverter))]
public readonly record struct GroupId
{
    public Guid Value { get; }

    public GroupId(Guid value)
    {
        if (value == Guid.Empty) throw new ArgumentException("Group ID cannot be empty", nameof(value));
        Value = value;
    }

    public static GroupId New() => new(Guid.NewGuid());

    public static GroupId Parse(string value)
    {
        if (!Guid.TryParseExact(value, "D", out var parsed) || parsed.ToString("D") != value)
            throw new FormatException("Group ID must use lowercase canonical UUID format");
        return new(parsed);
    }

    public override string ToString() => Value.ToString("D");
}

public sealed class GroupIdJsonConverter : JsonConverter<GroupId>
{
    public override GroupId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("Group ID must be a UUID string");
        try
        {
            return GroupId.Parse(reader.GetString()!);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            throw new JsonException("Group ID must be a non-empty canonical UUID", exception);
        }
    }

    public override void Write(Utf8JsonWriter writer, GroupId value, JsonSerializerOptions options)
    {
        if (value.Value == Guid.Empty) throw new JsonException("Group ID cannot be empty");
        writer.WriteStringValue(value.ToString());
    }
}
