using Forge.Host.Domain;
using Forge.Host.Games.UYA;

namespace Forge.Host.Bridge;

internal static class RenderBridgeHandlers
{
    public static async Task<byte[]> PrepareTieCollisionPreviewAsync(
        byte[] payload,
        string sdkRevision,
        EditorRuntime runtime,
        CancellationToken cancellationToken)
    {
        var request = EditorPayloadCodec.DecodeTieCollisionRenderRequest(payload);
        var candidate = await runtime.GetTieCollisionPreviewCandidateAsync(request.Token, cancellationToken);
        var result = await UyaTieCollisionPreviewService.PrepareRenderAsync(
            request.CacheRootPath,
            request.CatalogRootPath,
            sdkRevision,
            candidate,
            cancellationToken);
        return BridgePayloadCodec.EncodeAssetPreviewResult(new(
            result.RootPath, result.CacheKey, result.ModelPath, result.CacheHit));
    }

    public static async Task<byte[]> PrepareAssetPreviewAsync(
        byte[] payload,
        string sdkRevision,
        CancellationToken cancellationToken)
    {
        var request = BridgePayloadCodec.DecodeAssetPreviewRequest(payload);
        if (request.TargetGame != "UYA") throw new ArgumentException("Asset preview target must be UYA.");
        var kind = request.Kind switch
        {
            "moby" => AssetKind.Moby,
            "tie" => AssetKind.Tie,
            "shrub" => AssetKind.Shrub,
            "texture" => AssetKind.Texture,
            "sky" => AssetKind.Sky,
            _ => throw new ArgumentException($"Unknown asset preview kind: {request.Kind}."),
        };
        var result = await UyaAssetPreviewService.PrepareAsync(new(
            request.CacheRootPath,
            request.CatalogRootPath,
            AssetId.Parse(request.AssetId),
            kind,
            request.TargetGame,
            request.ViewPreset,
            request.ShellIndex is { } shellIndex ? checked((int)shellIndex) : null), sdkRevision, cancellationToken);
        return BridgePayloadCodec.EncodeAssetPreviewResult(new(
            result.RootPath, result.CacheKey, result.ModelPath, result.CacheHit));
    }

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
