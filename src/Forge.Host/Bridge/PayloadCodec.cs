using System.Buffers.Binary;
using System.Text;

namespace Forge.Host.Bridge;

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

    public static byte[] EncodeUyaIsoValidation(UyaIsoValidationPayload value)
    {
        var writer = new PayloadWriter();
        writer.WriteBoolean(value.IsSupported);
        writer.WriteString(value.Game);
        writer.WriteString(value.Region);
        writer.WriteString(value.Revision);
        writer.WriteString(value.Serial);
        writer.WriteUInt64(value.Size);
        writer.WriteString(value.Fingerprint);
        writer.WriteString(value.Diagnostic);
        return writer.ToArray();
    }

    public static UyaIsoValidationPayload DecodeUyaIsoValidation(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var value = new UyaIsoValidationPayload(
            reader.ReadBoolean(), reader.ReadString(), reader.ReadString(), reader.ReadString(),
            reader.ReadString(), reader.ReadUInt64(), reader.ReadString(), reader.ReadString());
        reader.Complete();
        return value;
    }

    public static byte[] EncodeDevelopmentIsoRequest(DevelopmentIsoRequest value)
    {
        var writer = new PayloadWriter();
        writer.WriteString(value.SourcePath);
        writer.WriteString(value.TargetPath);
        writer.WriteString(value.Fingerprint);
        writer.WriteBoolean(value.Overwrite);
        return writer.ToArray();
    }

    public static DevelopmentIsoRequest DecodeDevelopmentIsoRequest(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var value = new DevelopmentIsoRequest(
            reader.ReadString(), reader.ReadString(), reader.ReadString(), reader.ReadBoolean());
        reader.Complete();
        return value;
    }

    public static byte[] EncodeDevelopmentIso(DevelopmentIsoPayload value)
    {
        var writer = new PayloadWriter();
        writer.WriteString(value.Path);
        writer.WriteUInt64(value.Size);
        writer.WriteString(value.Fingerprint);
        return writer.ToArray();
    }

    public static DevelopmentIsoPayload DecodeDevelopmentIso(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var value = new DevelopmentIsoPayload(reader.ReadString(), reader.ReadUInt64(), reader.ReadString());
        reader.Complete();
        return value;
    }

    public static byte[] EncodeUyaAssetImportRequest(UyaAssetImportRequestPayload value)
    {
        var writer = new PayloadWriter();
        writer.WriteString(value.SourceIsoPath);
        writer.WriteString(value.CatalogRootPath);
        writer.WriteString(value.Fingerprint);
        writer.WriteString(value.Revision);
        writer.WriteBoolean(value.Force);
        return writer.ToArray();
    }

    public static UyaAssetImportRequestPayload DecodeUyaAssetImportRequest(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var value = new UyaAssetImportRequestPayload(
            reader.ReadString(), reader.ReadString(), reader.ReadString(), reader.ReadString(), reader.ReadBoolean());
        reader.Complete();
        return value;
    }

    public static byte[] EncodeUyaAssetImportResult(UyaAssetImportResultPayload value)
    {
        var writer = new PayloadWriter();
        writer.WriteUInt32(value.CompletedLevels);
        writer.WriteUInt32(value.TotalLevels);
        writer.WriteUInt32(value.AssetAppearances);
        writer.WriteUInt32(value.UniqueAssets);
        writer.WriteUInt32(value.FailedAssets);
        writer.WriteBoolean(value.Resumed);
        return writer.ToArray();
    }

    public static UyaAssetImportResultPayload DecodeUyaAssetImportResult(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var value = new UyaAssetImportResultPayload(
            reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadBoolean());
        reader.Complete();
        return value;
    }

    public static byte[] EncodeUyaProjectOptions(UyaProjectOptionsPayload value)
    {
        var writer = new PayloadWriter();
        writer.WriteUInt32s(value.Levels);
        writer.WriteStrings(value.Warnings);
        return writer.ToArray();
    }

    public static UyaProjectOptionsPayload DecodeUyaProjectOptions(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var value = new UyaProjectOptionsPayload(reader.ReadUInt32s(), reader.ReadStrings());
        reader.Complete();
        return value;
    }

    public static byte[] EncodeUyaProjectCreationRequest(UyaProjectCreationRequestPayload value)
    {
        var writer = new PayloadWriter();
        writer.WriteString(value.SourceIsoPath);
        writer.WriteString(value.CatalogRootPath);
        writer.WriteString(value.ProjectPath);
        writer.WriteString(value.Name);
        writer.WriteString(value.Fingerprint);
        writer.WriteString(value.Revision);
        writer.WriteUInt32(value.Level);
        writer.WriteBoolean(value.AllowPartial);
        return writer.ToArray();
    }

    public static UyaProjectCreationRequestPayload DecodeUyaProjectCreationRequest(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var value = new UyaProjectCreationRequestPayload(
            reader.ReadString(), reader.ReadString(), reader.ReadString(), reader.ReadString(),
            reader.ReadString(), reader.ReadString(), reader.ReadUInt32(), reader.ReadBoolean());
        reader.Complete();
        return value;
    }

    public static byte[] EncodeUyaProjectPreflightRequest(UyaProjectPreflightRequestPayload value)
    {
        var writer = new PayloadWriter();
        writer.WriteString(value.SourceIsoPath);
        writer.WriteString(value.CatalogRootPath);
        writer.WriteUInt32(value.Level);
        return writer.ToArray();
    }

    public static UyaProjectPreflightRequestPayload DecodeUyaProjectPreflightRequest(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var value = new UyaProjectPreflightRequestPayload(reader.ReadString(), reader.ReadString(), reader.ReadUInt32());
        reader.Complete();
        return value;
    }

    public static byte[] EncodeUyaProjectPreflight(UyaProjectPreflightPayload value)
    {
        var writer = new PayloadWriter();
        writer.WriteUInt32(value.Level);
        writer.WriteUInt32(value.SourceInstanceCount);
        writer.WriteUInt32(value.RenderableInstanceCount);
        writer.WriteUInt32(value.ModelLessInstanceCount);
        writer.WriteUInt32(value.MissingAssetInstanceCount);
        writer.WriteUInt32(value.MissingClassCount);
        writer.WriteStrings(value.Warnings);
        return writer.ToArray();
    }

    public static UyaProjectPreflightPayload DecodeUyaProjectPreflight(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var value = new UyaProjectPreflightPayload(
            reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(),
            reader.ReadUInt32(), reader.ReadStrings());
        reader.Complete();
        return value;
    }

    public static byte[] EncodeProjectInspectRequest(ProjectInspectRequestPayload value)
    {
        var writer = new PayloadWriter();
        writer.WriteString(value.ProjectPath);
        writer.WriteString(value.CatalogRootPath);
        return writer.ToArray();
    }

    public static ProjectInspectRequestPayload DecodeProjectInspectRequest(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var value = new ProjectInspectRequestPayload(reader.ReadString(), reader.ReadString());
        reader.Complete();
        return value;
    }

    public static byte[] EncodeProjectRenameRequest(ProjectRenameRequestPayload value)
    {
        var writer = new PayloadWriter();
        writer.WriteString(value.ProjectPath);
        writer.WriteString(value.CatalogRootPath);
        writer.WriteString(value.Name);
        return writer.ToArray();
    }

    public static ProjectRenameRequestPayload DecodeProjectRenameRequest(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var value = new ProjectRenameRequestPayload(reader.ReadString(), reader.ReadString(), reader.ReadString());
        reader.Complete();
        return value;
    }

    public static byte[] EncodeForgeProjectDescriptor(ForgeProjectDescriptorPayload value)
    {
        var writer = new PayloadWriter();
        writer.WriteString(value.Path);
        writer.WriteString(value.Name);
        writer.WriteString(value.TargetGame);
        writer.WriteString(value.TargetRegion);
        writer.WriteString(value.TargetRevision);
        writer.WriteString(value.BakeProfile);
        writer.WriteUInt32(value.BaseLevel);
        writer.WriteUInt64(value.ModifiedUnixMilliseconds);
        writer.WriteUInt32(value.EntityCount);
        writer.WriteUInt32(value.MissingAssetCount);
        writer.WriteStrings(value.Warnings);
        return writer.ToArray();
    }

    public static ForgeProjectDescriptorPayload DecodeForgeProjectDescriptor(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var value = new ForgeProjectDescriptorPayload(
            reader.ReadString(), reader.ReadString(), reader.ReadString(), reader.ReadString(), reader.ReadString(),
            reader.ReadString(), reader.ReadUInt32(), reader.ReadUInt64(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadStrings());
        reader.Complete();
        return value;
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

        public void WriteUInt32s(IReadOnlyList<uint> values)
        {
            if (values.Count > MaxListItems) Malformed("List exceeds item limit");
            WriteUInt32((uint)values.Count);
            foreach (var value in values) WriteUInt32(value);
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

        public uint[] ReadUInt32s()
        {
            var count = ReadUInt32();
            if (count > MaxListItems) Malformed("List exceeds item limit");
            var values = new uint[count];
            for (var index = 0; index < values.Length; index++) values[index] = ReadUInt32();
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
