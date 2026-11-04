using RatchetPs2.Core.LevelAssets;

namespace Forge.Host.Domain;

internal static class CatalogLevelAssetBuilder
{
    public static CatalogLevelAssets Create(LevelAssetExtractionResult extracted, AssetImportRequest request,
        int level, uint canonicalFormatVersion, string defaultArchivePath)
    {
        var game = request.Game.ToString();
        var results = new List<AssetCatalogPut>();
        var sounds = new Dictionary<(string Archive, int Index), AssetMobySoundUse>();
        foreach (var bank in extracted.SoundBanks)
        {
            var bankId = AssetId.Compute(AssetKind.SoundBank, SoundBankAssetSchema.CanonicalFormatVersion, bank.Bytes);
            results.AddRange(CatalogSoundAssets.Create(bank.Bytes, bank.Inventory,
                new(game, "NTSC-U", request.Revision, $"level{level:00}", bank.ArchivePath, 0, request.Fingerprint), request.ImporterVersion));
            var programsByIndex = bank.Inventory.Programs.ToDictionary(value => value.NativeIndex);
            foreach (var moby in bank.Mobys)
                sounds.Add((moby.ModelArchivePath, moby.SourceIndex), CatalogSoundAssets.MobyUse(bankId, programsByIndex, moby.ClassId, moby.Programs));
        }
        if (extracted.SkyboxBytes is { Length: > 0 } sky)
            results.Add(new(AssetKind.Sky, 0, sky, new(request.ImporterVersion,
                new(game, "NTSC-U", request.Revision, $"level{level:00}", defaultArchivePath, 0, request.Fingerprint),
                SkyShellCatalogService.Aliases(sky, request.Game), ["vanilla", $"game:{game}", $"level:{level:00}", "base-layer"])));
        foreach (var asset in extracted.Assets)
        {
            if (asset.ModelBytes.Length == 0) continue;
            var archive = asset.SourceArchivePath ?? defaultArchivePath;
            results.AddRange(CatalogModelAssets.Create(request.Game, canonicalFormatVersion, asset,
                new(game, "NTSC-U", request.Revision, $"level{level:00}", archive, asset.SourceIndex, request.Fingerprint,
                    MobySounds: asset.Kind == FrontendAssetKind.Moby
                        ? sounds.GetValueOrDefault((archive, asset.SourceIndex)) : null),
                request.ImporterVersion, [$"{asset.Kind.ToString().ToLowerInvariant()}:0x{asset.ClassId:X4}"]));
        }
        return new(results, extracted.FailedAssetCount);
    }
}
