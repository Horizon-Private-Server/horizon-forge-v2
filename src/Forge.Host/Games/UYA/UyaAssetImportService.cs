using Forge.Host.Domain;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.LevelAssets;
using RatchetPs2.Games.UYA.Level;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.UYA;

public static class UyaAssetImportService
{
    public const string ImporterVersionPrefix = "forge-uya-v8+";
    public const uint CanonicalFormatVersion = 1;
    public const uint TextureCanonicalFormatVersion = ProjectTextureAssetSchema.CanonicalFormatVersion;

    public static Task<AssetImportResult> ImportAsync(
        UyaAssetImportRequest request,
        Func<IsoProgress, ValueTask>? progress = null,
        CancellationToken cancellationToken = default) =>
        AssetImportService.ImportAsync(ToSharedRequest(request),
            (bytes, level) => ReadAssets(bytes, level, request), progress, cancellationToken);

    public static Task<AssetImportResult> ImportValidatedAsync(
        Stream iso,
        UyaAssetImportRequest request,
        Func<IsoProgress, ValueTask>? progress = null,
        CancellationToken cancellationToken = default) =>
        AssetImportService.ImportValidatedAsync(iso, ToSharedRequest(request),
            (bytes, level) => ReadAssets(bytes, level, request), progress, cancellationToken);

    private static AssetImportRequest ToSharedRequest(UyaAssetImportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(GameId.UYA, request.SourceIsoPath, request.CatalogRootPath, request.Fingerprint,
            request.Revision, request.ImporterVersion, request.Force);
    }

    public static async Task<AssetImportResult> RepairLevelsAsync(
        UyaAssetImportRequest request,
        IReadOnlyCollection<int> levels,
        Func<IsoProgress, ValueTask>? progress = null,
        CancellationToken cancellationToken = default)
    {
        AssetImportService.ValidateRequest(ToSharedRequest(request));
        var identity = await UyaIsoService.ValidateAsync(
            request.SourceIsoPath,
            progress is null ? null : value => AssetImportService.ReportScaledAsync(progress, value, 0, 2_000),
            cancellationToken);
        if (!identity.IsSupported || !identity.Fingerprint.Equals(request.Fingerprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The selected UYA source ISO does not match this project's verified source fingerprint.");

        await using var iso = new FileStream(
            request.SourceIsoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        return await RepairLevelsValidatedAsync(iso, request, levels, progress, cancellationToken);
    }

    public static async Task<AssetImportResult> RepairLevelsValidatedAsync(
        Stream iso,
        UyaAssetImportRequest request,
        IReadOnlyCollection<int> levels,
        Func<IsoProgress, ValueTask>? progress = null,
        CancellationToken cancellationToken = default)
    {
        AssetImportService.ValidateRequest(ToSharedRequest(request));
        ArgumentNullException.ThrowIfNull(iso);
        ArgumentNullException.ThrowIfNull(levels);
        if (!iso.CanRead || !iso.CanSeek)
            throw new ArgumentException("The UYA ISO stream must be readable and seekable.", nameof(iso));
        var requestedLevels = levels.Distinct().Order().ToArray();
        if (requestedLevels.Length == 0) return new(0, 0, 0, 0, 0, false);
        var availableLevels = LevelCatalogReader.FindAvailable(GameId.UYA, iso);
        if (requestedLevels.Any(level => !availableLevels.Contains(level)))
            throw new InvalidDataException("A repair level is not present in the selected UYA source ISO.");
        var store = await AssetCatalogStore.OpenAsync(request.CatalogRootPath, cancellationToken);
        var ids = new HashSet<AssetId>();
        var appearances = 0;
        var failures = 0;
        for (var index = 0; index < requestedLevels.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var levelAssets = ReadAssets(
                LevelArchiveReader.ExtractPrimary(GameId.UYA, iso, requestedLevels[index]),
                requestedLevels[index],
                request);
            foreach (var entry in await store.PutManyAsync(levelAssets.Assets, cancellationToken)) ids.Add(entry.Id);
            appearances = checked(appearances + levelAssets.Assets.Count);
            failures = checked(failures + levelAssets.FailedAssets);
            await AssetImportService.ReportAsync(progress, 2_000 + (index + 1) * 8_000 / requestedLevels.Length, 10_000);
        }
        return new(requestedLevels.Length, requestedLevels.Length, appearances, ids.Count, failures, false);
    }

    private static CatalogLevelAssets ReadAssets(
        byte[] levelWadBytes,
        int level,
        UyaAssetImportRequest request)
    {
        var package = UyaLevelWadUnpacker.Unpack(levelWadBytes);
        var extracted = LevelAssetExtractor.ExtractUnpacked(GameId.UYA, package.Files);
        var results = new List<AssetCatalogPut>(extracted.Assets.Count);
        var sounds = new Dictionary<int, AssetMobySoundUse>();
        foreach (var bank in extracted.SoundBanks)
        {
            var bankId = AssetId.Compute(AssetKind.SoundBank, SoundBankAssetSchema.CanonicalFormatVersion, bank.Bytes);
            results.AddRange(CatalogSoundAssets.Create(bank.Bytes, bank.Inventory,
                new("UYA", "NTSC-U", request.Revision, $"level{level:00}", bank.ArchivePath, 0, request.Fingerprint), request.ImporterVersion));
            var programsByIndex = bank.Inventory.Programs.ToDictionary(value => value.NativeIndex);
            foreach (var moby in bank.Mobys)
                sounds.Add(moby.SourceIndex, CatalogSoundAssets.MobyUse(bankId, programsByIndex, moby.ClassId, moby.Programs));
        }
        foreach (var asset in extracted.Assets)
        {
            var kindName = asset.Kind.ToString().ToLowerInvariant();
            results.AddRange(CatalogModelAssets.Create(GameId.UYA, CanonicalFormatVersion, asset,
                new("UYA", "NTSC-U", request.Revision, $"level{level:00}", "level_wad/assets/asset_wad.bin",
                    asset.SourceIndex, request.Fingerprint,
                    MobySounds: asset.Kind == FrontendAssetKind.Moby
                        ? sounds.GetValueOrDefault(asset.SourceIndex) : null),
                request.ImporterVersion, [$"{kindName}:{asset.ClassId}", $"{kindName}:0x{asset.ClassId:X4}"]));
        }
        if (extracted.SkyboxBytes is { Length: > 0 } sky)
            results.AddRange(UyaBaseLayerService.CreateCatalogPuts(
                [new(BakeLayerId.Sky, "sky.bin", AssetKind.Sky, "level_wad/assets/asset_wad.bin", 0, sky)],
                new("UYA", "NTSC-U", request.Revision, level, request.Fingerprint), request.ImporterVersion));
        if (UyaHudProjectService.TryRead(package) is { } hud)
            results.AddRange(UyaHudProjectService.CreateCatalogPuts(
                hud, level, request.Revision, request.Fingerprint, request.ImporterVersion));
        if (UyaFxProjectService.TryRead(package) is { } fx)
            results.AddRange(UyaFxProjectService.CreateCatalogPuts(
                fx, level, request.Revision, request.Fingerprint, request.ImporterVersion));
        return new(results, extracted.FailedAssetCount);
    }
}
