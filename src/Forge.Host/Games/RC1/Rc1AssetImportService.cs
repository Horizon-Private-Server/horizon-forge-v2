using Forge.Host.Domain;
using RatchetPs2.Core.Games;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.RC1;

public static class Rc1AssetImportService
{
    public const string ImporterVersionPrefix = "forge-rc1-v2+";
    public const uint CanonicalFormatVersion = 1;

    public static Task<AssetImportResult> ImportAsync(
        AssetImportRequest request,
        Func<IsoProgress, ValueTask>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateGame(request);
        return AssetImportService.ImportAsync(request, (bytes, level) => ReadAssets(bytes, level, request), progress, cancellationToken);
    }

    public static Task<AssetImportResult> ImportValidatedAsync(
        Stream iso,
        AssetImportRequest request,
        Func<IsoProgress, ValueTask>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateGame(request);
        return AssetImportService.ImportValidatedAsync(iso, request, (bytes, level) => ReadAssets(bytes, level, request), progress, cancellationToken);
    }

    private static void ValidateGame(AssetImportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Game != GameId.RC1) throw new ArgumentException("RC1 import requires a RC1 source.");
    }

    private static CatalogLevelAssets ReadAssets(byte[] wad, int level, AssetImportRequest request) =>
        CatalogLevelAssetBuilder.Create(LevelAssetExtractor.ExtractLevelWad(GameId.RC1, wad), request,
            level, CanonicalFormatVersion, "assets/asset_wad.bin");
}
