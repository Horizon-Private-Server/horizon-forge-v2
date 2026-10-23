using System.Buffers.Binary;

namespace Forge.Host.Games.UYA;

internal sealed record UyaOcclusionImportSource(
    byte[] GameplayMappings,
    byte[] AssetGrid,
    byte[] LevelOcclusion);

internal static class UyaOcclusionImportPayload
{
    private static ReadOnlySpan<byte> Magic => "UYOCCL1\0"u8;
    private const int HeaderSize = 0x20;

    public static byte[] Encode(UyaOcclusionImportSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.GameplayMappings.Length == 0 || source.AssetGrid.Length == 0
            || source.LevelOcclusion.Length == 0)
            throw new InvalidDataException("A UYA occlusion import requires mappings, grid, and level payloads.");
        var output = new byte[checked(HeaderSize + source.GameplayMappings.Length
            + source.AssetGrid.Length + source.LevelOcclusion.Length)];
        Magic.CopyTo(output);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(0x08), 1);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(0x0c), source.GameplayMappings.Length);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(0x10), source.AssetGrid.Length);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(0x14), source.LevelOcclusion.Length);
        var offset = HeaderSize;
        source.GameplayMappings.CopyTo(output, offset);
        offset += source.GameplayMappings.Length;
        source.AssetGrid.CopyTo(output, offset);
        offset += source.AssetGrid.Length;
        source.LevelOcclusion.CopyTo(output, offset);
        return output;
    }

    public static UyaOcclusionImportSource Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderSize || !bytes[..Magic.Length].SequenceEqual(Magic)
            || BinaryPrimitives.ReadInt32LittleEndian(bytes[0x08..]) != 1)
            throw new InvalidDataException("Imported UYA occlusion payload header is invalid.");
        var mappingsLength = ReadLength(bytes, 0x0c);
        var gridLength = ReadLength(bytes, 0x10);
        var levelLength = ReadLength(bytes, 0x14);
        var total = checked(HeaderSize + mappingsLength + gridLength + levelLength);
        if (total != bytes.Length || mappingsLength == 0 || gridLength == 0 || levelLength == 0)
            throw new InvalidDataException("Imported UYA occlusion payload lengths are invalid.");
        var offset = HeaderSize;
        var mappings = bytes.Slice(offset, mappingsLength).ToArray();
        offset += mappingsLength;
        var grid = bytes.Slice(offset, gridLength).ToArray();
        offset += gridLength;
        return new(mappings, grid, bytes.Slice(offset, levelLength).ToArray());
    }

    public static async Task<UyaOcclusionImportSource?> ReadAsync(
        Forge.Host.Domain.ForgeProjectWorkspace workspace,
        CancellationToken cancellationToken)
    {
        if (workspace.Content.Occlusion is not { } state) return null;
        var bytes = await workspace.ReadAttachedAssetVerifiedAsync(
            state.SourcePayload,
            Forge.Host.Domain.ForgeProjectWorkspace.MaxAttachedAssetBytes,
            cancellationToken);
        return Decode(bytes);
    }

    private static int ReadLength(ReadOnlySpan<byte> bytes, int offset)
    {
        var value = BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]);
        if (value < 0) throw new InvalidDataException("Imported UYA occlusion payload length is negative.");
        return value;
    }
}
