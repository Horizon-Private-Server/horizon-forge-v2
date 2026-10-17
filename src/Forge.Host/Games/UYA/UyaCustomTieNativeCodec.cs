using System.Buffers.Binary;

namespace Forge.Host.Games.UYA;

internal static class UyaCustomTieNativeCodec
{
    private const int HeaderSize = 20;
    private static ReadOnlySpan<byte> Magic => "HFUYTIE1"u8;

    public static byte[] Write(ReadOnlySpan<byte> definition, ReadOnlySpan<byte> model)
    {
        if (definition.IsEmpty || model.IsEmpty)
            throw new ArgumentException("Custom TIE native sections cannot be empty.");
        var bytes = GC.AllocateUninitializedArray<byte>(checked(HeaderSize + definition.Length + model.Length));
        Magic.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), ProjectCustomTieNativeVersion);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), definition.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), model.Length);
        definition.CopyTo(bytes.AsSpan(HeaderSize));
        model.CopyTo(bytes.AsSpan(HeaderSize + definition.Length));
        return bytes;
    }

    public static UyaCustomTieNative Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderSize || !bytes[..Magic.Length].SequenceEqual(Magic)
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) != ProjectCustomTieNativeVersion)
            throw new InvalidDataException("Custom TIE native envelope is invalid.");
        var definitionLength = BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]);
        var modelLength = BinaryPrimitives.ReadInt32LittleEndian(bytes[16..]);
        if (definitionLength <= 0 || modelLength <= 0
            || (long)HeaderSize + definitionLength + modelLength != bytes.Length)
            throw new InvalidDataException("Custom TIE native envelope lengths are invalid.");
        return new(
            bytes.Slice(HeaderSize, definitionLength).ToArray(),
            bytes.Slice(HeaderSize + definitionLength, modelLength).ToArray());
    }

    private const uint ProjectCustomTieNativeVersion = 1;
}

internal sealed record UyaCustomTieNative(byte[] DefinitionBytes, byte[] ModelBytes);
