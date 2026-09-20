using System.Buffers.Binary;
using System.Text;

namespace Forge.Host.Bridge;

public sealed record HostHandshake(
    string HostVersion,
    string SdkRevision,
    IReadOnlyList<string> SupportedGames,
    IReadOnlyList<string> Capabilities);

public readonly record struct EchoRequest(string Message, uint DelayMs);
public readonly record struct BridgeProgress(uint Completed, uint Total);

public static class BridgePayloadCodec
{
    public const uint MaxEchoDelayMs = 60_000;
    private const int MaxTextBytes = 1024 * 1024;
    private const int MaxListItems = 64;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] EncodeHandshake(HostHandshake handshake)
    {
        var writer = new PayloadWriter();
        writer.WriteString(handshake.HostVersion);
        writer.WriteString(handshake.SdkRevision);
        writer.WriteStrings(handshake.SupportedGames);
        writer.WriteStrings(handshake.Capabilities);
        return writer.ToArray();
    }

    public static HostHandshake DecodeHandshake(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var value = new HostHandshake(
            reader.ReadString(),
            reader.ReadString(),
            reader.ReadStrings(),
            reader.ReadStrings());
        reader.Complete();
        return value;
    }

    public static byte[] EncodeEchoRequest(string message, uint delayMs = 0)
    {
        if (delayMs > MaxEchoDelayMs) Malformed("Echo delay exceeds limit");
        var writer = new PayloadWriter();
        writer.WriteUInt32(delayMs);
        writer.WriteString(message);
        return writer.ToArray();
    }

    public static EchoRequest DecodeEchoRequest(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var delayMs = reader.ReadUInt32();
        if (delayMs > MaxEchoDelayMs) Malformed("Echo delay exceeds limit");
        var value = new EchoRequest(reader.ReadString(), delayMs);
        reader.Complete();
        return value;
    }

    public static byte[] EncodeText(string value)
    {
        var writer = new PayloadWriter();
        writer.WriteString(value);
        return writer.ToArray();
    }

    public static string DecodeText(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var value = reader.ReadString();
        reader.Complete();
        return value;
    }

    public static byte[] EncodeProgress(uint completed, uint total)
    {
        if (total == 0 || completed > total) Malformed("Invalid progress values");
        var payload = GC.AllocateUninitializedArray<byte>(8);
        BinaryPrimitives.WriteUInt32LittleEndian(payload, completed);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), total);
        return payload;
    }

    public static BridgeProgress DecodeProgress(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 8) Malformed("Progress payload must contain eight bytes");
        var completed = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        var total = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        if (total == 0 || completed > total) Malformed("Invalid progress values");
        return new(completed, total);
    }

    private static void Malformed(string message) =>
        throw new BridgeProtocolException(BridgeErrorCode.MalformedPayload, message);

    private sealed class PayloadWriter
    {
        private readonly MemoryStream _stream = new();

        public void WriteUInt32(uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            _stream.Write(bytes);
        }

        public void WriteString(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            var bytes = Encoding.UTF8.GetBytes(value);
            if (bytes.Length > MaxTextBytes) Malformed("Text field exceeds limit");
            WriteUInt32((uint)bytes.Length);
            _stream.Write(bytes);
        }

        public void WriteStrings(IReadOnlyList<string> values)
        {
            if (values.Count > MaxListItems) Malformed("List exceeds item limit");
            WriteUInt32((uint)values.Count);
            foreach (var value in values) WriteString(value);
        }

        public byte[] ToArray() => _stream.ToArray();
    }

    private ref struct PayloadReader(ReadOnlySpan<byte> payload)
    {
        private readonly ReadOnlySpan<byte> _payload = payload;
        private int _offset;

        public uint ReadUInt32()
        {
            Require(4);
            var value = BinaryPrimitives.ReadUInt32LittleEndian(_payload[_offset..]);
            _offset += 4;
            return value;
        }

        public string ReadString()
        {
            var length = ReadUInt32();
            if (length > MaxTextBytes) Malformed("Text field exceeds limit");
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
            if (count > MaxListItems) Malformed("List exceeds item limit");
            var values = new string[count];
            for (var index = 0; index < values.Length; index++) values[index] = ReadString();
            return values;
        }

        public void Complete()
        {
            if (_offset != _payload.Length) Malformed("Payload has trailing bytes");
        }

        private void Require(int length)
        {
            if (length > _payload.Length - _offset) Malformed("Payload ended unexpectedly");
        }
    }
}
