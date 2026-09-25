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
        writer.WriteBoolean(value.SkyPath is not null);
        if (value.SkyPath is not null) writer.WriteString(value.SkyPath);
        writer.WriteBoolean(value.Environment is not null);
        if (value.Environment is { } environment)
        {
            writer.WriteUInt32(environment.BackgroundRed);
            writer.WriteUInt32(environment.BackgroundGreen);
            writer.WriteUInt32(environment.BackgroundBlue);
            writer.WriteUInt32(environment.FogRed);
            writer.WriteUInt32(environment.FogGreen);
            writer.WriteUInt32(environment.FogBlue);
            writer.WriteSingle(environment.FogNearDistance);
            writer.WriteSingle(environment.FogFarDistance);
            writer.WriteSingle(environment.FogNearIntensity);
            writer.WriteSingle(environment.FogFarIntensity);
            writer.WriteSingle(environment.DeathHeight);
            writer.WriteBoolean(environment.IsSphericalWorld);
            writer.WriteSingle(environment.SphereCenterX);
            writer.WriteSingle(environment.SphereCenterY);
            writer.WriteSingle(environment.SphereCenterZ);
            writer.WriteSingle(environment.ShipPositionX);
            writer.WriteSingle(environment.ShipPositionY);
            writer.WriteSingle(environment.ShipPositionZ);
            writer.WriteSingle(environment.ShipRotationZ);
            writer.WriteInt32(environment.ShipPath);
            writer.WriteInt32(environment.ShipCameraCuboidStart);
            writer.WriteInt32(environment.ShipCameraCuboidEnd);
            writer.WriteInt32(environment.ChunkPlaneCount);
            writer.WriteInt32(environment.CoreSoundsCount);
        }
        writer.WriteUInt32(checked((uint)value.Assets.Count));
        foreach (var asset in value.Assets)
        {
            writer.WriteString(asset.AssetId);
            writer.WriteString(asset.Kind);
            writer.WriteBoolean(asset.Path is not null);
            if (asset.Path is not null) writer.WriteString(asset.Path);
            writer.WriteBoolean(asset.Error is not null);
            if (asset.Error is not null) writer.WriteString(asset.Error);
        }
        var octants = value.OcclusionOctants ?? [];
        writer.WriteUInt32(checked((uint)octants.Count));
        foreach (var octant in octants)
        {
            writer.WriteInt32(octant.X);
            writer.WriteInt32(octant.Y);
            writer.WriteInt32(octant.Z);
            writer.WriteInt32(octant.MaskIndex);
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
        var skyPath = reader.ReadBoolean() ? reader.ReadString() : null;
        var environment = reader.ReadBoolean()
            ? new UyaRenderEnvironmentPayload(
                reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(),
                reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(),
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
                reader.ReadSingle(), reader.ReadBoolean(),
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
                reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32())
            : null;
        var assetCount = reader.ReadUInt32();
        if (assetCount > 100_000) PayloadFormat.Malformed("Render asset list exceeds item limit");
        var assets = new UyaRenderAssetPayload[assetCount];
        for (var index = 0; index < assets.Length; index++)
            assets[index] = new(
                reader.ReadString(),
                reader.ReadString(),
                reader.ReadBoolean() ? reader.ReadString() : null,
                reader.ReadBoolean() ? reader.ReadString() : null);
        var octantCount = reader.ReadUInt32();
        if (octantCount > 1_000_000) PayloadFormat.Malformed("Occlusion octant list exceeds item limit");
        var octants = new UyaRenderOcclusionOctantPayload[octantCount];
        for (var index = 0; index < octants.Length; index++)
            octants[index] = new(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
        var value = new UyaRenderPackageResultPayload(
            rootPath, cacheKey, terrainPaths, skyPath, environment, assets, reader.ReadBoolean(), octants);
        reader.Complete();
        return value;
    }
}
