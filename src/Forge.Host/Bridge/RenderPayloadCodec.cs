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
        writer.WriteString(value.ProjectPath);
        writer.WriteString(value.CatalogRootPath);
        return writer.ToArray();
    }

    public static UyaRenderPackageRequestPayload DecodeUyaRenderPackageRequest(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var value = new UyaRenderPackageRequestPayload(
            reader.ReadString(), reader.ReadString(), reader.ReadString(), reader.ReadUInt32(),
            reader.ReadString(), reader.ReadString());
        reader.Complete();
        return value;
    }

    public static byte[] EncodeUyaRenderPackageResult(UyaRenderPackageResultPayload value)
    {
        var writer = new PayloadWriter();
        writer.WriteString(value.RootPath);
        writer.WriteString(value.CacheKey);
        writer.WriteStrings(value.TerrainPaths);
        writer.WriteUInt32(checked((uint)value.Assets.Count));
        foreach (var asset in value.Assets)
        {
            writer.WriteString(asset.AssetId);
            writer.WriteBoolean(asset.Path is not null);
            if (asset.Path is not null) writer.WriteString(asset.Path);
            writer.WriteBoolean(asset.Error is not null);
            if (asset.Error is not null) writer.WriteString(asset.Error);
        }
        writer.WriteBoolean(value.CacheHit);
        return writer.ToArray();
    }

    public static UyaRenderPackageResultPayload DecodeUyaRenderPackageResult(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var rootPath = reader.ReadString();
        var cacheKey = reader.ReadString();
        var terrainPaths = reader.ReadStrings();
        var assetCount = reader.ReadUInt32();
        if (assetCount > 100_000) PayloadFormat.Malformed("Render asset list exceeds item limit");
        var assets = new UyaRenderAssetPayload[assetCount];
        for (var index = 0; index < assets.Length; index++)
            assets[index] = new(
                reader.ReadString(),
                reader.ReadBoolean() ? reader.ReadString() : null,
                reader.ReadBoolean() ? reader.ReadString() : null);
        var value = new UyaRenderPackageResultPayload(
            rootPath, cacheKey, terrainPaths, assets, reader.ReadBoolean());
        reader.Complete();
        return value;
    }
}
