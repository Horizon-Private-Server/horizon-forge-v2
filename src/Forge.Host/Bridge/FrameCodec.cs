using System.Buffers.Binary;
using System.Text;

namespace Forge.Host.Bridge;

public enum BridgeMessageKind : byte
{
    Handshake = 1,
    Request = 2,
    Result = 3,
    Progress = 4,
    Cancel = 5,
    Error = 6,
}

public enum BridgeOpcode : ushort
{
    Control = 0,
    Echo = 1,
}

public enum BridgeErrorCode : ushort
{
    None = 0,
    InvalidMagic = 1,
    UnsupportedVersion = 2,
    InvalidMessageKind = 3,
    InvalidFlags = 4,
    UnknownOpcode = 5,
    InvalidStatus = 6,
    InvalidRequestId = 7,
    PayloadTooLarge = 8,
    InvalidHeader = 9,
    MalformedPayload = 10,
    Cancelled = 11,
    InternalError = 12,
}

public sealed record BridgeFrame(
    BridgeMessageKind Kind,
    BridgeOpcode Opcode,
    BridgeErrorCode Status,
    uint RequestId,
    byte[] Payload);

public sealed class BridgeProtocolException(BridgeErrorCode code, string message) : Exception(message)
{
    public BridgeErrorCode Code { get; } = code;
}

public static class BridgeFrameCodec
{
    public const int HeaderSize = 24;
    public const int MaxPayloadLength = 64 * 1024 * 1024;
    public const ushort ProtocolVersion = 1;

    private static ReadOnlySpan<byte> Magic => "HFG2"u8;

    public static byte[] Encode(BridgeFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame.Payload);
        ValidateHeader(frame.Kind, 0, frame.Opcode, frame.Status, frame.RequestId, frame.Payload.Length, 0);
        if (frame.Kind == BridgeMessageKind.Error) BridgeErrorPayload.Decode(frame.Payload);

        var encoded = GC.AllocateUninitializedArray<byte>(HeaderSize + frame.Payload.Length);
        Magic.CopyTo(encoded);
        BinaryPrimitives.WriteUInt16LittleEndian(encoded.AsSpan(4), ProtocolVersion);
        encoded[6] = (byte)frame.Kind;
        encoded[7] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(encoded.AsSpan(8), (ushort)frame.Opcode);
        BinaryPrimitives.WriteUInt16LittleEndian(encoded.AsSpan(10), (ushort)frame.Status);
        BinaryPrimitives.WriteUInt32LittleEndian(encoded.AsSpan(12), frame.RequestId);
        BinaryPrimitives.WriteUInt32LittleEndian(encoded.AsSpan(16), (uint)frame.Payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(encoded.AsSpan(20), 0);
        frame.Payload.CopyTo(encoded, HeaderSize);
        return encoded;
    }

    internal static DecodedHeader DecodeHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length != HeaderSize)
        {
            throw new BridgeProtocolException(BridgeErrorCode.InvalidHeader, $"Header must contain {HeaderSize} bytes");
        }
        if (!header[..4].SequenceEqual(Magic))
        {
            throw new BridgeProtocolException(BridgeErrorCode.InvalidMagic, "Invalid bridge magic");
        }

        var version = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
        if (version != ProtocolVersion)
        {
            throw new BridgeProtocolException(BridgeErrorCode.UnsupportedVersion, $"Unsupported bridge version: {version}");
        }

        var kind = (BridgeMessageKind)header[6];
        var flags = header[7];
        var opcode = (BridgeOpcode)BinaryPrimitives.ReadUInt16LittleEndian(header[8..]);
        var status = (BridgeErrorCode)BinaryPrimitives.ReadUInt16LittleEndian(header[10..]);
        var requestId = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
        var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
        var reserved = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
        ValidateHeader(kind, flags, opcode, status, requestId, payloadLength, reserved);
        return new(kind, opcode, status, requestId, checked((int)payloadLength));
    }

    private static void ValidateHeader(
        BridgeMessageKind kind,
        byte flags,
        BridgeOpcode opcode,
        BridgeErrorCode status,
        uint requestId,
        long payloadLength,
        uint reserved)
    {
        if (!Enum.IsDefined(kind)) Throw(BridgeErrorCode.InvalidMessageKind, $"Unknown message kind: {(byte)kind}");
        if (flags != 0) Throw(BridgeErrorCode.InvalidFlags, $"Unsupported flags: {flags}");
        if (!Enum.IsDefined(opcode)) Throw(BridgeErrorCode.UnknownOpcode, $"Unknown opcode: {(ushort)opcode}");
        if (reserved != 0) Throw(BridgeErrorCode.InvalidHeader, "Reserved header bytes must be zero");
        if (payloadLength is < 0 or > MaxPayloadLength)
        {
            Throw(BridgeErrorCode.PayloadTooLarge, $"Invalid payload length: {payloadLength}");
        }

        if (kind == BridgeMessageKind.Error)
        {
            if (status == BridgeErrorCode.None || !Enum.IsDefined(status))
            {
                Throw(BridgeErrorCode.InvalidStatus, $"Invalid error status: {(ushort)status}");
            }
        }
        else if (status != BridgeErrorCode.None)
        {
            Throw(BridgeErrorCode.InvalidStatus, $"Non-error frame has status: {(ushort)status}");
        }

        if (kind == BridgeMessageKind.Handshake)
        {
            if (opcode != BridgeOpcode.Control || requestId != 0)
            {
                Throw(BridgeErrorCode.InvalidRequestId, "Handshake must use control opcode and request ID zero");
            }
        }
        else
        {
            if (requestId == 0) Throw(BridgeErrorCode.InvalidRequestId, "Non-handshake request ID must be nonzero");
            if (kind != BridgeMessageKind.Error && opcode == BridgeOpcode.Control)
            {
                Throw(BridgeErrorCode.UnknownOpcode, "Control opcode is only valid for handshake and error frames");
            }
        }

        if (kind == BridgeMessageKind.Cancel && payloadLength != 0)
        {
            Throw(BridgeErrorCode.MalformedPayload, "Cancellation payload must be empty");
        }
    }

    private static void Throw(BridgeErrorCode code, string message) => throw new BridgeProtocolException(code, message);
}

public sealed class BridgeFrameDecoder
{
    private readonly byte[] _header = GC.AllocateUninitializedArray<byte>(BridgeFrameCodec.HeaderSize);
    private int _headerBytes;
    private DecodedHeader? _pending;
    private byte[]? _payload;
    private int _payloadBytes;

    public IReadOnlyList<BridgeFrame> Push(ReadOnlySpan<byte> chunk)
    {
        var frames = new List<BridgeFrame>();

        while (!chunk.IsEmpty)
        {
            if (_pending is null)
            {
                var count = Math.Min(BridgeFrameCodec.HeaderSize - _headerBytes, chunk.Length);
                chunk[..count].CopyTo(_header.AsSpan(_headerBytes));
                _headerBytes += count;
                chunk = chunk[count..];
                if (_headerBytes < BridgeFrameCodec.HeaderSize) continue;

                _pending = BridgeFrameCodec.DecodeHeader(_header);
                _headerBytes = 0;
                if (_pending.Value.PayloadLength == 0)
                {
                    frames.Add(CompleteFrame([]));
                    continue;
                }
                _payload = GC.AllocateUninitializedArray<byte>(_pending.Value.PayloadLength);
            }

            var payloadLength = _pending.Value.PayloadLength;
            var payloadCount = Math.Min(payloadLength - _payloadBytes, chunk.Length);
            chunk[..payloadCount].CopyTo(_payload.AsSpan(_payloadBytes));
            _payloadBytes += payloadCount;
            chunk = chunk[payloadCount..];
            if (_payloadBytes == payloadLength) frames.Add(CompleteFrame(_payload!));
        }

        return frames;
    }

    public void Complete()
    {
        if (_headerBytes != 0 || _pending is not null)
        {
            throw new BridgeProtocolException(BridgeErrorCode.MalformedPayload, "Stream ended during a frame");
        }
    }

    private BridgeFrame CompleteFrame(byte[] payload)
    {
        var header = _pending!.Value;
        if (header.Kind == BridgeMessageKind.Error) BridgeErrorPayload.Decode(payload);
        var frame = new BridgeFrame(header.Kind, header.Opcode, header.Status, header.RequestId, payload);
        _pending = null;
        _payload = null;
        _payloadBytes = 0;
        return frame;
    }
}

public static class BridgeErrorPayload
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] Encode(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var bytes = Encoding.UTF8.GetBytes(message);
        if (bytes.Length == 0)
        {
            throw new BridgeProtocolException(BridgeErrorCode.MalformedPayload, "Error message must not be empty");
        }
        var payload = GC.AllocateUninitializedArray<byte>(4 + bytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)bytes.Length);
        bytes.CopyTo(payload, 4);
        return payload;
    }

    public static string Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4 || BinaryPrimitives.ReadUInt32LittleEndian(payload) != payload.Length - 4)
        {
            throw new BridgeProtocolException(BridgeErrorCode.MalformedPayload, "Invalid error message length");
        }
        try
        {
            var message = StrictUtf8.GetString(payload[4..]);
            return message.Length > 0
                ? message
                : throw new BridgeProtocolException(BridgeErrorCode.MalformedPayload, "Error message must not be empty");
        }
        catch (DecoderFallbackException exception)
        {
            throw new BridgeProtocolException(BridgeErrorCode.MalformedPayload, $"Error message is not valid UTF-8: {exception.Message}");
        }
    }
}

internal readonly record struct DecodedHeader(
    BridgeMessageKind Kind,
    BridgeOpcode Opcode,
    BridgeErrorCode Status,
    uint RequestId,
    int PayloadLength);
