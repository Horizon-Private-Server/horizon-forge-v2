using Forge.Host.Domain;
using Forge.Host.Games.UYA;

namespace Forge.Host.Bridge;

internal static class RenderBridgeHandlers
{
    public static async Task<byte[]> PrepareUyaAsync(
        byte[] payload,
        string sdkRevision,
        Func<IsoProgress, ValueTask> progress,
        CancellationToken cancellationToken)
    {
        var request = BridgePayloadCodec.DecodeUyaRenderPackageRequest(payload);
        var result = await UyaRenderPackageService.PrepareAsync(new(
            request.SourceIsoPath,
            request.CacheRootPath,
            request.Fingerprint,
            checked((int)request.Level),
            request.ProjectPath,
            request.CatalogRootPath), sdkRevision, progress, cancellationToken);
        return BridgePayloadCodec.EncodeUyaRenderPackageResult(new(
            result.RootPath, result.CacheKey, result.TerrainPaths,
            result.SkyPath,
            result.Environment is { } environment ? new(
                environment.BackgroundRed, environment.BackgroundGreen, environment.BackgroundBlue,
                environment.FogRed, environment.FogGreen, environment.FogBlue,
                environment.FogNearDistance, environment.FogFarDistance,
                environment.FogNearIntensity, environment.FogFarIntensity,
                environment.DeathHeight, environment.IsSphericalWorld,
                environment.SphereCenterX, environment.SphereCenterY, environment.SphereCenterZ,
                environment.ShipPositionX, environment.ShipPositionY, environment.ShipPositionZ,
                environment.ShipRotationZ, environment.ShipPath,
                environment.ShipCameraCuboidStart, environment.ShipCameraCuboidEnd,
                environment.ChunkPlaneCount, environment.CoreSoundsCount) : null,
            result.Assets.Select(asset => new UyaRenderAssetPayload(
                asset.AssetId, asset.Kind, asset.Path, asset.Error)).ToArray(),
            result.CacheHit,
            result.OcclusionOctants?.Select(value => new UyaRenderOcclusionOctantPayload(
                value.X, value.Y, value.Z, value.MaskIndex)).ToArray()));
    }
}
