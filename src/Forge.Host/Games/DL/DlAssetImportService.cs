using Forge.Host.Domain;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.LevelAssets;
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

    private static CatalogLevelAssets ReadAssets(byte[] wad, int level, AssetImportRequest request)
    {
        var extracted = LevelAssetExtractor.ExtractLevelWad(GameId.DL, wad);
        var results = new List<AssetCatalogPut>();
        var sounds = new Dictionary<(string Archive, int Index), AssetMobySoundUse>();
        foreach (var bank in extracted.SoundBanks)
        {
            var bankId = AssetId.Compute(AssetKind.SoundBank, SoundBankAssetSchema.CanonicalFormatVersion, bank.Bytes);
            results.AddRange(CatalogSoundAssets.Create(bank.Bytes, bank.Inventory,
                new("DL", "NTSC-U", request.Revision, $"level{level:00}", bank.ArchivePath, 0, request.Fingerprint), request.ImporterVersion));
            var programsByIndex = bank.Inventory.Programs.ToDictionary(value => value.NativeIndex);
            foreach (var moby in bank.Mobys)
                sounds.Add((moby.ModelArchivePath, moby.SourceIndex), CatalogSoundAssets.MobyUse(bankId, programsByIndex, moby.ClassId, moby.Programs));
        }
        if (extracted.SkyboxBytes is { Length: > 0 } sky)
            results.Add(new(AssetKind.Sky, 0, sky, new(request.ImporterVersion,
                new("DL", "NTSC-U", request.Revision, $"level{level:00}", "assets/asset_wad.bin", 0, request.Fingerprint),
                SkyShellCatalogService.Aliases(sky, GameId.DL), ["vanilla", "game:DL", $"level:{level:00}", "base-layer"])));
        foreach (var asset in extracted.Assets)
        {
            if (asset.ModelBytes.Length == 0) continue;
            var archive = asset.SourceArchivePath ?? "assets/asset_wad.bin";
            results.AddRange(CatalogModelAssets.Create(GameId.DL, CanonicalFormatVersion, asset,
                new("DL", "NTSC-U", request.Revision, $"level{level:00}", archive, asset.SourceIndex, request.Fingerprint,
                    MobySounds: asset.Kind == FrontendAssetKind.Moby
                        ? sounds.GetValueOrDefault((archive, asset.SourceIndex)) : null),
                request.ImporterVersion, [$"{asset.Kind.ToString().ToLowerInvariant()}:0x{asset.ClassId:X4}"]));
        }
        return new(results, extracted.FailedAssetCount);
    }
}
