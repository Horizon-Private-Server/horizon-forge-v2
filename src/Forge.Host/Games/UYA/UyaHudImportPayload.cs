using System.Buffers.Binary;
using Forge.Host.Domain;
using RatchetPs2.Core.Hud;
using RatchetPs2.Core.IO;
using RatchetPs2.Core.Wad;
using RatchetPs2.Core.Wad.Models;

namespace Forge.Host.Games.UYA;

internal sealed record UyaHudImportPayloadData(
    byte[] Header,
    IReadOnlyList<byte[]> Banks);

internal static class UyaHudImportPayload
{
    public const uint CanonicalFormatVersion = ProjectHudSchema.SourcePayloadCanonicalFormatVersion;
    private const uint Magic = 0x44485546; // FUHD
    private const int HeaderSize = 0x20;

    public static byte[] Encode(IReadOnlyList<OpaqueSectionCapture> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        var header = Single(sections, "level-data/hud-header");
        var banks = Enumerable.Range(0, ProjectHudSchema.PhysicalBankCount)
            .Select(index => OptionalSingle(sections, $"level-data/hud-bank-{index}"))
            .ToArray();
        return Encode(header, banks);
    }

    internal static byte[] Encode(byte[] header, IReadOnlyList<byte[]> banks)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(banks);
        if (header.Length == 0 || banks.Count != ProjectHudSchema.PhysicalBankCount)
            throw new InvalidDataException("Imported HUD payload is incomplete.");
        var length = checked(HeaderSize + header.Length + banks.Sum(value => value.Length));
        if (length > ForgeProjectWorkspace.MaxAttachedAssetBytes)
            throw new InvalidDataException("Imported HUD payload exceeds the attached-asset limit.");
        var output = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(output, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4), CanonicalFormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(8), header.Length);
        for (var index = 0; index < banks.Count; index++)
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(0xc + index * sizeof(int)), banks[index].Length);
        var offset = HeaderSize;
        header.CopyTo(output.AsSpan(offset));
        offset += header.Length;
        foreach (var bank in banks)
        {
            bank.CopyTo(output.AsSpan(offset));
            offset += bank.Length;
        }
        _ = Decode(output);
        return output;
    }

    public static UyaHudImportPayloadData Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderSize
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != CanonicalFormatVersion)
            throw new InvalidDataException("Imported HUD payload header is invalid.");
        var lengths = new int[ProjectHudSchema.PhysicalBankCount + 1];
        for (var index = 0; index < lengths.Length; index++)
            lengths[index] = BinaryPrimitives.ReadInt32LittleEndian(bytes[(8 + index * sizeof(int))..]);
        if (lengths[0] <= 0 || lengths.Skip(1).Any(value => value < 0)
            || lengths.Sum(value => (long)value) != bytes.Length - HeaderSize)
            throw new InvalidDataException("Imported HUD payload lengths are invalid.");
        var offset = HeaderSize;
        var header = bytes.Slice(offset, lengths[0]).ToArray();
        offset += lengths[0];
        var banks = new byte[ProjectHudSchema.PhysicalBankCount][];
        for (var index = 0; index < banks.Length; index++)
        {
            var length = lengths[index + 1];
            var value = bytes.Slice(offset, length).ToArray();
            offset += length;
            banks[index] = value;
        }
        var decodedBanks = banks.Select(value => BinaryMagic.IsWad(value)
            ? WadCompression.Decompress(value, new WadDecompressionOptions(64 * 1024 * 1024))
            : value).ToArray();
        _ = HudBankReader.Read(header, decodedBanks);
        return new(header, banks);
    }

    private static byte[] Single(IReadOnlyList<OpaqueSectionCapture> sections, string name)
    {
        var matches = sections.Where(value => value.Name == name).Take(2).ToArray();
        return matches.Length == 1
            ? matches[0].Bytes
            : throw new InvalidDataException($"Donor HUD section {name} is missing or duplicated.");
    }

    private static byte[] OptionalSingle(IReadOnlyList<OpaqueSectionCapture> sections, string name)
    {
        var matches = sections.Where(value => value.Name == name).Take(2).ToArray();
        return matches.Length switch
        {
            0 => [],
            1 => matches[0].Bytes,
            _ => throw new InvalidDataException($"Donor HUD section {name} is duplicated."),
        };
    }
}
