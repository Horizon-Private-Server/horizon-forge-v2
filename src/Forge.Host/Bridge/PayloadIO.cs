using System.Buffers.Binary;
using System.Text;

namespace Forge.Host.Bridge;

internal static class PayloadFormat
{
    public const int MaxTextBytes = 1024 * 1024;
    public const int MaxListItems = 64;

    public static void Malformed(string message) =>
        throw new BridgeProtocolException(BridgeErrorCode.MalformedPayload, message);
}

internal sealed class PayloadWriter
{
    private readonly MemoryStream _stream = new();

    public void WriteUInt32(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        _stream.Write(bytes);
    }

    public void WriteUInt64(ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        _stream.Write(bytes);
    }

    public void WriteBoolean(bool value) => _stream.WriteByte(value ? (byte)1 : (byte)0);

    public void WriteString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > PayloadFormat.MaxTextBytes) PayloadFormat.Malformed("Text field exceeds limit");
        WriteUInt32((uint)bytes.Length);
        _stream.Write(bytes);
    }

    public void WriteStrings(IReadOnlyList<string> values)
    {
        if (values.Count > PayloadFormat.MaxListItems) PayloadFormat.Malformed("List exceeds item limit");
        WriteUInt32((uint)values.Count);
        foreach (var value in values) WriteString(value);
    }

    public void WriteUInt32s(IReadOnlyList<uint> values)
    {
        if (values.Count > PayloadFormat.MaxListItems) PayloadFormat.Malformed("List exceeds item limit");
        WriteUInt32((uint)values.Count);
        foreach (var value in values) WriteUInt32(value);
    }

    public byte[] ToArray() => _stream.ToArray();
}

internal ref struct PayloadReader(ReadOnlySpan<byte> payload)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly ReadOnlySpan<byte> _payload = payload;
    private int _offset;

    public uint ReadUInt32()
    {
        Require(4);
        var value = BinaryPrimitives.ReadUInt32LittleEndian(_payload[_offset..]);
        _offset += 4;
        return value;
    }

    public ulong ReadUInt64()
    {
        Require(8);
        var value = BinaryPrimitives.ReadUInt64LittleEndian(_payload[_offset..]);
        _offset += 8;
        return value;
    }

    public bool ReadBoolean()
    {
        Require(1);
        return _payload[_offset++] switch
        {
            0 => false,
            1 => true,
            _ => throw new BridgeProtocolException(BridgeErrorCode.MalformedPayload, "Boolean field must be zero or one"),
        };
    }

    public string ReadString()
    {
        var length = ReadUInt32();
        if (length > PayloadFormat.MaxTextBytes) PayloadFormat.Malformed("Text field exceeds limit");
        Require((int)length);
        try
        {
            var value = StrictUtf8.GetString(_payload.Slice(_offset, (int)length));
            _offset += (int)length;
            return value;
        }
        catch (DecoderFallbackException exception)
        {
            throw new BridgeProtocolException(BridgeErrorCode.MalformedPayload, $"Text field is not valid UTF-8: {exception.Message}");
        }
    }

    public string[] ReadStrings()
    {
        var count = ReadUInt32();
        if (count > PayloadFormat.MaxListItems) PayloadFormat.Malformed("List exceeds item limit");
        var values = new string[count];
        for (var index = 0; index < values.Length; index++) values[index] = ReadString();
        return values;
    }

    public uint[] ReadUInt32s()
    {
        var count = ReadUInt32();
        if (count > PayloadFormat.MaxListItems) PayloadFormat.Malformed("List exceeds item limit");
        var values = new uint[count];
        for (var index = 0; index < values.Length; index++) values[index] = ReadUInt32();
        return values;
    }

    public void Complete()
    {
        if (_offset != _payload.Length) PayloadFormat.Malformed("Payload has trailing bytes");
    }

    private void Require(int length)
    {
        if (length > _payload.Length - _offset) PayloadFormat.Malformed("Payload ended unexpectedly");
    }
}
