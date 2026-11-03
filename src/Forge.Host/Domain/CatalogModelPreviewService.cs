using RatchetPs2.Core.LevelAssets;
using RatchetPs2.Core.Wad.Models;
using RatchetPs2.Sdk;

namespace Forge.Host.Domain;

internal static class CatalogModelPreviewService
{
    public static Task<PackedFilePackage> BuildAsync(AssetKind kind, byte[] bytes, CancellationToken cancellationToken)
    {
        var canonical = CatalogModelCodec.Decode(bytes);
        var frontendKind = kind switch
        {
            AssetKind.Moby => FrontendAssetKind.Moby,
            AssetKind.Tie => FrontendAssetKind.Tie,
            AssetKind.Shrub => FrontendAssetKind.Shrub,
            _ => throw new NotSupportedException($"{kind} render assets are not supported."),
        };
        return Task.Run(() => FrontendAssetPackageBuilder.Build(
            canonical.Game, frontendKind, canonical.ModelBytes, canonical.Textures,
            new(PreserveAuthoredRepeatedTexCoordTiles: kind == AssetKind.Tie)), cancellationToken);
    }
}
