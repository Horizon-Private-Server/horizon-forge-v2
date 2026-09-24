using Forge.Host.Domain;
using System.Buffers.Binary;
using RatchetPs2.Core.LevelAssets;

namespace Forge.Host.Games.UYA;

internal sealed record UyaCanonicalAsset(
    byte[] DefinitionBytes,
    byte[] ModelBytes,
    IReadOnlyList<FrontendAssetTexture> Textures);

internal static class UyaCanonicalAssetCodec
{
    private static ReadOnlySpan<byte> Magic => "HFUYA1"u8;
    private static ReadOnlySpan<byte> LegacyMagic => "HFUYA\0"u8;

    public static byte[] Encode(
        ReadOnlySpan<byte> definitionBytes,
        ReadOnlySpan<byte> modelBytes,
        IReadOnlyList<FrontendAssetTexture> textures)
    {
        if (definitionBytes.Length is not 0x20 and not 0x30)
            throw new ArgumentException("Canonical UYA definition must be 0x20 or 0x30 bytes.", nameof(definitionBytes));
        if (modelBytes.IsEmpty) throw new ArgumentException("Canonical UYA model cannot be empty.", nameof(modelBytes));
        ArgumentNullException.ThrowIfNull(textures);
        if (textures.Count > 4_096 || textures.Any(value => value is null || value.Role > 1 || value.PifBytes.Length == 0))
            throw new ArgumentException("Canonical UYA textures are invalid.", nameof(textures));
        var definition = definitionBytes.ToArray();
        definition.AsSpan(0, 8).Clear();
        definition.AsSpan(0x10).Fill(byte.MaxValue);
        using var stream = new MemoryStream();
        stream.Write(Magic);
        WriteLength(stream, definition.Length);
        stream.Write(definition);
        WriteLength(stream, modelBytes.Length);
        stream.Write(modelBytes);
        WriteLength(stream, textures.Count);
        foreach (var texture in textures)
        {
            stream.WriteByte(texture.Role);
            WriteLength(stream, texture.PifBytes.Length);
            stream.Write(texture.PifBytes);
        }
        return stream.ToArray();
    }

    public static UyaCanonicalAsset Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 14
            || (!bytes[..Magic.Length].SequenceEqual(Magic)
                && !bytes[..LegacyMagic.Length].SequenceEqual(LegacyMagic)))
            throw new InvalidDataException("Asset blob has an invalid UYA canonical header.");
        var legacy = bytes[..LegacyMagic.Length].SequenceEqual(LegacyMagic);
        var offset = Magic.Length;
        var definition = Array.Empty<byte>();
        if (!legacy)
        {
            var definitionLength = ReadLength(bytes, ref offset, "definition");
            if (definitionLength is not 0x20 and not 0x30)
                throw new InvalidDataException("Asset blob definition has an invalid size.");
            definition = bytes.Slice(offset, definitionLength).ToArray();
            offset += definitionLength;
        }
        var modelLength = ReadLength(bytes, ref offset, "model");
        if (modelLength == 0) throw new InvalidDataException("Asset blob model is empty.");
        var model = bytes.Slice(offset, modelLength).ToArray();
        offset += modelLength;
        var textureCount = ReadLength(bytes, ref offset, "texture count");
        if (textureCount > 4_096) throw new InvalidDataException("Asset blob texture count exceeds the limit.");
        var textures = new FrontendAssetTexture[textureCount];
        for (var index = 0; index < textures.Length; index++)
        {
            if (offset >= bytes.Length) throw new InvalidDataException("Asset blob ended before its texture role.");
            var role = bytes[offset++];
            if (role > 1) throw new InvalidDataException($"Asset blob texture {index} has an invalid role.");
            var length = ReadLength(bytes, ref offset, "texture");
            if (length == 0) throw new InvalidDataException($"Asset blob texture {index} is empty.");
            textures[index] = new(role, bytes.Slice(offset, length).ToArray());
            offset += length;
        }
        if (offset != bytes.Length) throw new InvalidDataException("Asset blob contains trailing data.");
        return new(definition, model, textures);
    }

    public static bool IsValid(ReadOnlySpan<byte> bytes)
    {
        try
        {
            _ = Decode(bytes);
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or OverflowException)
        {
            return false;
        }
    }

    private static int ReadLength(ReadOnlySpan<byte> bytes, ref int offset, string field)
    {
        if (offset > bytes.Length - sizeof(int))
            throw new InvalidDataException($"Asset blob ended before its {field} length.");
        var length = BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]);
        offset += sizeof(int);
        if (length < 0 || length > bytes.Length - offset)
            throw new InvalidDataException($"Asset blob {field} length is invalid.");
        return length;
    }

    private static void WriteLength(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }
}
