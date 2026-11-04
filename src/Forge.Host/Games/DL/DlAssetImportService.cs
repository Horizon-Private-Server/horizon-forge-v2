using Forge.Host.Domain;
using RatchetPs2.Core.Games;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.DL;

public static class DlAssetImportService
{
    public const string ImporterVersionPrefix = "forge-dl-v3+";
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
        if (request.Game != GameId.DL) throw new ArgumentException("DL import requires a DL source.");
    }

    private static CatalogLevelAssets ReadAssets(byte[] wad, int level, AssetImportRequest request) =>
        CatalogLevelAssetBuilder.Create(LevelAssetExtractor.ExtractLevelWad(GameId.DL, wad), request,
            level, CanonicalFormatVersion, "assets/asset_wad.bin");
}
