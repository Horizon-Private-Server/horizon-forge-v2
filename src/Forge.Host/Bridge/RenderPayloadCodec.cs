namespace Forge.Host.Bridge;

public static partial class BridgePayloadCodec
{
    public static byte[] EncodeUyaRenderPackageRequest(UyaRenderPackageRequestPayload value)
    {
        var writer = new PayloadWriter();
        writer.WriteString(value.SourceIsoPath);
        writer.WriteString(value.CacheRootPath);
        writer.WriteString(value.Fingerprint);
        writer.WriteUInt32(value.Level);
        return writer.ToArray();
    }

    public static UyaRenderPackageRequestPayload DecodeUyaRenderPackageRequest(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var value = new UyaRenderPackageRequestPayload(
            reader.ReadString(), reader.ReadString(), reader.ReadString(), reader.ReadUInt32());
        reader.Complete();
        return value;
    }

    public static byte[] EncodeUyaRenderPackageResult(UyaRenderPackageResultPayload value)
    {
        var writer = new PayloadWriter();
        writer.WriteString(value.RootPath);
        writer.WriteString(value.CacheKey);
        writer.WriteStrings(value.TerrainPaths);
        writer.WriteBoolean(value.CacheHit);
        return writer.ToArray();
    }

    public static UyaRenderPackageResultPayload DecodeUyaRenderPackageResult(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var value = new UyaRenderPackageResultPayload(
            reader.ReadString(), reader.ReadString(), reader.ReadStrings(), reader.ReadBoolean());
        reader.Complete();
        return value;
    }
}
