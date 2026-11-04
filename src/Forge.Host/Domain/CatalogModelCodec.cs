using System.Buffers.Binary;
using RatchetPs2.Core.LevelAssets;
using RatchetPs2.Core.Games;
using RatchetPs2.Sdk;

namespace Forge.Host.Domain;

internal sealed record CatalogModel(
    GameId Game,
    byte[] DefinitionBytes,
    byte[] ModelBytes,
    IReadOnlyList<FrontendAssetTexture> Textures);

internal static class CatalogModelCodec
{
    private static ReadOnlySpan<byte> Magic(GameId game) => game switch
    {
        GameId.UYA => "HFUYA1"u8,
        GameId.DL => "HFDL01"u8,
        GameId.GC => "HFGC01"u8,
        _ => throw new NotSupportedException($"Canonical assets are not supported for {game}."),
    };
    private static ReadOnlySpan<byte> LegacyMagic => "HFUYA\0"u8;

    public static byte[] Encode(
        GameId game,
        ReadOnlySpan<byte> definitionBytes,
        ReadOnlySpan<byte> modelBytes,
        IReadOnlyList<FrontendAssetTexture> textures)
    {
        LevelAssetCanonicalizer.Validate(game, definitionBytes, modelBytes, textures);
        var definition = LevelAssetCanonicalizer.NormalizeDefinition(game, definitionBytes);
        using var stream = new MemoryStream();
        stream.Write(Magic(game));
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

    public static CatalogModel Decode(ReadOnlySpan<byte> bytes)
    {
        var game = bytes.StartsWith(Magic(GameId.DL)) ? GameId.DL
            : bytes.StartsWith(Magic(GameId.GC)) ? GameId.GC : GameId.UYA;
        var magic = Magic(game);
        if (bytes.Length < 14
            || (!bytes[..magic.Length].SequenceEqual(magic)
                && !bytes[..LegacyMagic.Length].SequenceEqual(LegacyMagic)))
            throw new InvalidDataException("Asset blob has an invalid canonical model header.");
        var legacy = bytes[..LegacyMagic.Length].SequenceEqual(LegacyMagic);
        var offset = magic.Length;
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
        if (!legacy) LevelAssetCanonicalizer.Validate(game, definition, model, textures);
        return new(game, definition, model, textures);
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
