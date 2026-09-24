using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Forge.Host.Domain;

public enum AssetKind : ushort
{
    Texture = 1,
    Material = 2,
    Tie = 3,
    Shrub = 4,
    Tfrag = 5,
    Moby = 6,
    Animation = 7,
    Sound = 8,
    Sky = 9,
    Gameplay = 10,
    World = 11,
    Collision = 12,
    Lighting = 13,
}

[JsonConverter(typeof(AssetIdJsonConverter))]
public readonly record struct AssetId
{
    public const int ByteLength = 32;
    public const int TextLength = ByteLength * 2;

    private readonly string _value;

    private AssetId(string value) => _value = value;

    public static AssetId Compute(AssetKind kind, uint canonicalFormatVersion, ReadOnlySpan<byte> canonicalBytes)
    {
        Span<byte> header = stackalloc byte[AssetIdentity.HeaderLength];
        AssetIdentity.WriteHeader(header, kind, canonicalFormatVersion);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(header);
        hash.AppendData(canonicalBytes);
        return new(Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    public static AssetId Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length != TextLength)
        {
            throw new FormatException("Asset ID must be a 64-character SHA-256 hexadecimal value");
        }

        Convert.FromHexString(value);
        return new(value.ToLowerInvariant());
    }

    public override string ToString() => _value ?? string.Empty;
}

public sealed class AssetIdJsonConverter : JsonConverter<AssetId>
{
    public override AssetId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("Asset ID must be a SHA-256 string");
        var value = reader.GetString()!;
        try
        {
            var id = AssetId.Parse(value);
            if (id.ToString() != value) throw new FormatException();
            return id;
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            throw new JsonException("Asset ID must be lowercase 64-character hexadecimal SHA-256", exception);
        }
    }

    public override void Write(Utf8JsonWriter writer, AssetId value, JsonSerializerOptions options)
    {
        if (value.ToString().Length != AssetId.TextLength) throw new JsonException("Asset ID cannot be empty");
        writer.WriteStringValue(value.ToString());
    }
}

public static class AssetIdentity
{
    public const ushort LayoutVersion = 0;
    public const int HeaderLength = 12;
    private static ReadOnlySpan<byte> Magic => "HFAS"u8;

    public static byte[] CreatePreimage(AssetKind kind, uint canonicalFormatVersion, ReadOnlySpan<byte> canonicalBytes)
    {
        var preimage = GC.AllocateUninitializedArray<byte>(checked(HeaderLength + canonicalBytes.Length));
        WriteHeader(preimage.AsSpan(0, HeaderLength), kind, canonicalFormatVersion);
        canonicalBytes.CopyTo(preimage.AsSpan(HeaderLength));
        return preimage;
    }

    internal static void WriteHeader(Span<byte> header, AssetKind kind, uint canonicalFormatVersion)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown asset kind");
        if (header.Length != HeaderLength) throw new ArgumentException($"Header must contain {HeaderLength} bytes", nameof(header));

        Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], LayoutVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], (ushort)kind);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], canonicalFormatVersion);
    }
}
