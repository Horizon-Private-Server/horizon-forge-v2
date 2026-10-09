using Forge.Host.Domain;
using RatchetPs2.Sdk;

namespace Forge.Host.Bridge;

public static partial class BridgePayloadCodec
{
    private const uint MaxFxTextures = 4_096;

    public static byte[] EncodeFxTextureInventory(FxTextureInventoryPayload value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Entries.Count > MaxFxTextures) PayloadFormat.Malformed("FX texture inventory exceeds item limit");
        var writer = new PayloadWriter();
        writer.WriteString(value.Game);
        writer.WriteBoolean(value.CanRead);
        writer.WriteBoolean(value.CanReplace);
        writer.WriteBoolean(value.CanAppend);
        writer.WriteString(value.AuthoringDisabledReason ?? string.Empty);
        writer.WriteUInt32((uint)value.Entries.Count);
        foreach (var entry in value.Entries)
        {
            writer.WriteInt32(entry.Index);
            writer.WriteString(entry.Label);
            writer.WriteInt32(entry.Width);
            writer.WriteInt32(entry.Height);
            writer.WriteString(entry.PixelFormat);
            writer.WriteString(entry.PaletteFormat);
            writer.WriteInt32(entry.PaletteOffset);
            writer.WriteInt32(entry.PaletteLength);
            writer.WriteInt32(entry.PixelOffset);
            writer.WriteInt32(entry.PixelLength);
            writer.WriteBoolean(entry.IsSwizzled);
            writer.WriteBoolean(entry.IsValid);
            writer.WriteString(entry.SourceAssetId ?? string.Empty);
            writer.WriteString(entry.Diagnostic ?? string.Empty);
        }
        return writer.ToArray();
    }

    public static FxTextureInventoryPayload DecodeFxTextureInventory(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var game = reader.ReadString();
        var canRead = reader.ReadBoolean();
        var canReplace = reader.ReadBoolean();
        var canAppend = reader.ReadBoolean();
        var authoringDisabledReason = NullIfEmpty(reader.ReadString());
        var count = reader.ReadUInt32();
        if (count > MaxFxTextures) PayloadFormat.Malformed("FX texture inventory exceeds item limit");
        var entries = new FxTextureInventoryItemPayload[count];
        for (var index = 0; index < entries.Length; index++)
        {
            entries[index] = new(
                reader.ReadInt32(),
                reader.ReadString(),
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadString(),
                reader.ReadString(),
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadBoolean(),
                reader.ReadBoolean(),
                NullIfEmpty(reader.ReadString()),
                NullIfEmpty(reader.ReadString()));
        }
        reader.Complete();
        return new(game, canRead, canReplace, canAppend, authoringDisabledReason, entries);
    }

    internal static FxTextureInventoryPayload ToPayload(FxTextureInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        return new(
            inventory.Game.ToString(),
            inventory.Capabilities.CanRead,
            inventory.Capabilities.CanReplace,
            inventory.Capabilities.CanAppend,
            inventory.Capabilities.AuthoringDisabledReason,
            inventory.Entries.Select(entry => new FxTextureInventoryItemPayload(
                entry.Index,
                entry.Label,
                entry.Width,
                entry.Height,
                entry.PixelFormat,
                entry.PaletteFormat,
                entry.PaletteOffset,
                entry.PaletteLength,
                entry.PixelOffset,
                entry.PixelLength,
                entry.IsSwizzled,
                entry.IsValid,
                entry.IsValid
                    ? AssetId.Compute(
                        AssetKind.Texture,
                        ProjectTextureAssetSchema.CanonicalFormatVersion,
                        entry.CanonicalTextureBytes).ToString()
                    : null,
                entry.Diagnostic)).ToArray());
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
