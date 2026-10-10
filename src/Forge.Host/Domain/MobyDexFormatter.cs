using System.Buffers;
using System.Text.Json;

namespace Forge.Host.Domain;

internal static class MobyDexFormatter
{
    public static byte[] Format(MobyDexEntry entry)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", entry.SchemaVersion);
            writer.WriteString("game", entry.Game);
            writer.WriteNumber("oClass", entry.OClass);
            writer.WriteString("name", entry.Name);
            if (entry.Description is not null) writer.WriteString("description", entry.Description);
            writer.WriteStartArray("tags");
            foreach (var tag in entry.Tags.Order(StringComparer.Ordinal)) writer.WriteStringValue(tag);
            writer.WriteEndArray();
            WritePVar(writer, entry.PVar!);
            writer.WriteEndObject();
        }
        var output = GC.AllocateUninitializedArray<byte>(buffer.WrittenCount + 1);
        buffer.WrittenSpan.CopyTo(output);
        output[^1] = (byte)'\n';
        return output;
    }

    private static void WritePVar(Utf8JsonWriter writer, MobyDexPVar pvar)
    {
        writer.WriteStartObject("pvar");
        writer.WriteNumber("length", pvar.Length);
        if (pvar.DefaultHex is not null) writer.WriteString("defaultHex", pvar.DefaultHex.ToLowerInvariant());
        writer.WriteStartArray("relocations");
        foreach (var relocation in pvar.Relocations.Order()) writer.WriteNumberValue(relocation);
        writer.WriteEndArray();
        writer.WriteStartArray("fields");
        foreach (var field in pvar.Fields) WriteField(writer, field);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteField(Utf8JsonWriter writer, MobyDexField field)
    {
        writer.WriteStartObject();
        writer.WriteString("key", field.Key);
        writer.WriteString("label", field.Label);
        writer.WriteNumber("offset", field.Offset);
        writer.WriteNumber("length", field.Length);
        if (field.Help is not null) writer.WriteString("help", field.Help);
        if (field.Minimum is { } minimum) writer.WriteNumber("minimum", minimum);
        if (field.Maximum is { } maximum) writer.WriteNumber("maximum", maximum);
        writer.WritePropertyName("definition");
        WriteDefinition(writer, field.Definition!);
        writer.WriteEndObject();
    }

    private static void WriteDefinition(Utf8JsonWriter writer, MobyDexTypeDefinition definition)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", definition.Kind);
        if (definition.Storage is not null) writer.WriteString("storage", definition.Storage);
        if (definition.Options is not null)
        {
            writer.WriteStartArray("options");
            foreach (var option in definition.Options)
            {
                writer.WriteStartObject();
                writer.WriteString("key", option.Key);
                writer.WriteString("label", option.Label);
                writer.WriteNumber("value", option.Value);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        if (definition.Count is { } count) writer.WriteNumber("count", count);
        if (definition.Stride is { } stride) writer.WriteNumber("stride", stride);
        if (definition.Element is not null)
        {
            writer.WritePropertyName("element");
            WriteDefinition(writer, definition.Element);
        }
        if (definition.Fields is not null)
        {
            writer.WriteStartArray("fields");
            foreach (var field in definition.Fields) WriteField(writer, field);
            writer.WriteEndArray();
        }
        if (definition.Domain is not null) writer.WriteString("domain", definition.Domain);
        if (definition.TargetKind is not null) writer.WriteString("targetKind", definition.TargetKind);
        if (definition.NullValue is { } nullValue) writer.WriteNumber("nullValue", nullValue);
        writer.WriteEndObject();
    }
}
