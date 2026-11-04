using Forge.Host.Domain;
using RatchetPs2.Core.Games;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.GC;

public static class GcAssetImportService
{
    public const string ImporterVersionPrefix = "forge-gc-v1+";
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
        if (request.Game != GameId.GC) throw new ArgumentException("GC import requires a GC source.");
    }

    private static CatalogLevelAssets ReadAssets(byte[] wad, int level, AssetImportRequest request) =>
        CatalogLevelAssetBuilder.Create(LevelAssetExtractor.ExtractLevelWad(GameId.GC, wad), request,
            level, CanonicalFormatVersion, "level_wad/assets/asset_wad.bin");
}
