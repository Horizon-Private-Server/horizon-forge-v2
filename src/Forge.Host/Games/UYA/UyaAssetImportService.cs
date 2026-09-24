using Forge.Host.Domain;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.LevelAssets;
using RatchetPs2.Core.Textures.Pif;
using RatchetPs2.Games.UYA.Level;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.UYA;

public static class UyaAssetImportService
{
    public const uint CanonicalFormatVersion = 1;
    private const int StateSchemaVersion = 0;
    private static readonly byte[] MissingTexturePif = CreateMissingTexturePif();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static async Task<UyaAssetImportResult> ImportAsync(
        UyaAssetImportRequest request,
        Func<IsoProgress, ValueTask>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        var statePath = GetStatePath(request);
        var state = await ReadStateAsync(statePath, request, cancellationToken);
        if (state?.Complete == true && !request.Force
            && await AssetsAvailableAsync(state, request.CatalogRootPath, cancellationToken))
            return Result(state, resumed: true);

        var identity = await UyaIsoService.ValidateAsync(
            request.SourceIsoPath,
            progress is null ? null : value => ReportScaledAsync(progress, value, 0, 2_000),
            cancellationToken);
        if (!identity.IsSupported || !identity.Fingerprint.Equals(request.Fingerprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The UYA source ISO no longer matches its verified fingerprint.");

        await using var iso = new FileStream(
            request.SourceIsoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        return await ImportValidatedAsync(
            iso,
            request,
            progress is null ? null : value => ReportScaledAsync(progress, value, 2_000, 10_000),
            cancellationToken);
    }

    public static async Task<UyaAssetImportResult> ImportValidatedAsync(
        Stream iso,
        UyaAssetImportRequest request,
        Func<IsoProgress, ValueTask>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        ArgumentNullException.ThrowIfNull(iso);
        if (!iso.CanRead || !iso.CanSeek)
            throw new ArgumentException("The UYA ISO stream must be readable and seekable.", nameof(iso));

        var statePath = GetStatePath(request);
        var state = await ReadStateAsync(statePath, request, cancellationToken);
        if (request.Force && state?.Complete == true) state = null;
        else if (state?.Complete == true
            && !await AssetsAvailableAsync(state, request.CatalogRootPath, cancellationToken)) state = null;
        state ??= new ImportState(StateSchemaVersion, request.Fingerprint, request.ImporterVersion, [], [], 0, 0, false);
        if (state.Complete) return Result(state, resumed: true);

        var resumed = state.CompletedLevels.Count > 0;
        var levels = LevelCatalogReader.FindAvailable(GameId.UYA, iso);
        var completed = state.CompletedLevels.ToHashSet();
        if (completed.Any(level => !levels.Contains(level)))
            throw new InvalidDataException("UYA asset import state contains levels not present in the source ISO.");
        var assetIds = state.AssetIds.ToHashSet(StringComparer.Ordinal);
        var appearances = state.AssetAppearances;
        var failedAssets = state.FailedAssets;
        var store = await AssetCatalogStore.OpenAsync(request.CatalogRootPath, cancellationToken);

        await ReportAsync(progress, completed.Count, levels.Count);
        foreach (var level in levels)
        {
            if (completed.Contains(level)) continue;
            cancellationToken.ThrowIfCancellationRequested();

            var levelWad = LevelArchiveReader.ExtractPrimary(GameId.UYA, iso, level);
            var levelAssets = ReadAssets(levelWad, level, request);
            var entries = await store.PutManyAsync(levelAssets.Assets, cancellationToken);
            foreach (var entry in entries) assetIds.Add(entry.Id.ToString());
            appearances = checked(appearances + levelAssets.Assets.Count);
            failedAssets = checked(failedAssets + levelAssets.FailedAssets);
            completed.Add(level);

            state = new(
                StateSchemaVersion,
                request.Fingerprint,
                request.ImporterVersion,
                completed.Order().ToList(),
                assetIds.Order(StringComparer.Ordinal).ToList(),
                appearances,
                failedAssets,
                completed.Count == levels.Count);
            await WriteStateAsync(statePath, state, cancellationToken);
            await ReportAsync(progress, completed.Count, levels.Count);
        }

        if (levels.Count == 0)
            throw new InvalidDataException("The UYA ISO does not contain any level WADs.");
        return Result(state, resumed);
    }

    public static async Task<UyaAssetImportResult> RepairLevelsAsync(
        UyaAssetImportRequest request,
        IReadOnlyCollection<int> levels,
        Func<IsoProgress, ValueTask>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        var identity = await UyaIsoService.ValidateAsync(
            request.SourceIsoPath,
            progress is null ? null : value => ReportScaledAsync(progress, value, 0, 2_000),
            cancellationToken);
        if (!identity.IsSupported || !identity.Fingerprint.Equals(request.Fingerprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The selected UYA source ISO does not match this project's verified source fingerprint.");

        await using var iso = new FileStream(
            request.SourceIsoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        return await RepairLevelsValidatedAsync(iso, request, levels, progress, cancellationToken);
    }

    public static async Task<UyaAssetImportResult> RepairLevelsValidatedAsync(
        Stream iso,
        UyaAssetImportRequest request,
        IReadOnlyCollection<int> levels,
        Func<IsoProgress, ValueTask>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
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
            await ReportAsync(progress, 2_000 + (index + 1) * 8_000 / requestedLevels.Length, 10_000);
        }
        return new(requestedLevels.Length, requestedLevels.Length, appearances, ids.Count, failures, false);
    }

    private static LevelAssets ReadAssets(
        byte[] levelWadBytes,
        int level,
        UyaAssetImportRequest request)
    {
        var extracted = LevelAssetExtractor.ExtractLevelWad(GameId.UYA, levelWadBytes);
        var results = new List<AssetCatalogPut>(extracted.Assets.Count);
        foreach (var asset in extracted.Assets)
        {
            var usedPlaceholder = asset.Textures.Any(texture => texture.PifBytes is null);
            AddAsset(
                results,
                asset.Kind switch
                {
                    FrontendAssetKind.Moby => AssetKind.Moby,
                    FrontendAssetKind.Tie => AssetKind.Tie,
                    FrontendAssetKind.Shrub => AssetKind.Shrub,
                    _ => throw new ArgumentOutOfRangeException(nameof(asset.Kind)),
                },
                asset.ClassId,
                asset.SourceIndex,
                asset.DefinitionBytes,
                asset.ModelBytes,
                asset.Textures.Select(texture => (texture.Role, texture.PifBytes ?? MissingTexturePif)).ToArray(),
                usedPlaceholder,
                level,
                request);
        }
        return new(results, extracted.FailedAssetCount);
    }

    private static void AddAsset(
        List<AssetCatalogPut> results,
        AssetKind kind,
        int modelId,
        int sourceIndex,
        byte[] definitionBytes,
        byte[] modelBytes,
        IReadOnlyList<(byte Role, byte[] Bytes)> textures,
        bool usedPlaceholder,
        int level,
        UyaAssetImportRequest request)
    {
        if (modelBytes.Length == 0) return;
        var kindName = kind.ToString().ToLowerInvariant();
        results.Add(new(
            kind,
            CanonicalFormatVersion,
            UyaCanonicalAssetCodec.Encode(
                definitionBytes,
                modelBytes,
                textures.Select(value => new FrontendAssetTexture(value.Role, value.Bytes)).ToArray()),
            new(
                request.ImporterVersion,
                new("UYA", "NTSC-U", request.Revision, $"level{level:00}", "level_wad/assets/asset_wad.bin", sourceIndex, request.Fingerprint),
                [$"{kindName}:{modelId}", $"{kindName}:0x{modelId:X4}"],
                usedPlaceholder
                    ? ["vanilla", "game:UYA", $"level:{level:00}", "texture:placeholder"]
                    : ["vanilla", "game:UYA", $"level:{level:00}"])));
    }

    private static byte[] CreateMissingTexturePif()
    {
        const int size = 8;
        var palette = new byte[0x400];
        new byte[] { 255, 0, 255, 255, 0, 0, 0, 255 }.CopyTo(palette, 0);
        var pixels = new byte[size * size];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
            pixels[y * size + x] = (byte)(((x / 2) + (y / 2)) & 1);
        return PifWriter.Write(PifWriter.CreateIndexed8(size, size, palette, pixels));
    }

    private static string GetStatePath(UyaAssetImportRequest request)
    {
        var versionHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.ImporterVersion)))
            .ToLowerInvariant();
        return Path.Combine(request.CatalogRootPath, "imports", "uya", request.Fingerprint, $"{versionHash}.json");
    }

    private static async Task<ImportState?> ReadStateAsync(
        string path,
        UyaAssetImportRequest request,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        ImportState state;
        try
        {
            state = await JsonSerializer.DeserializeAsync<ImportState>(stream, JsonOptions, cancellationToken)
                ?? throw new InvalidDataException("UYA asset import state is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("UYA asset import state contains invalid JSON.", exception);
        }
        if (state.SchemaVersion != StateSchemaVersion
            || state.Fingerprint != request.Fingerprint
            || state.ImporterVersion != request.ImporterVersion)
            throw new InvalidDataException("UYA asset import state does not match this importer.");
        if (state.CompletedLevels is null || state.AssetIds is null || state.AssetAppearances < 0 || state.FailedAssets < 0
            || state.CompletedLevels.Count != state.CompletedLevels.Distinct().Count()
            || state.CompletedLevels.Any(level => level < 0 || level >= UyaLevelConstants.LevelInfoCount))
            throw new InvalidDataException("UYA asset import state contains invalid progress data.");
        try
        {
            foreach (var id in state.AssetIds) AssetId.Parse(id);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            throw new InvalidDataException("UYA asset import state contains an invalid Asset ID.", exception);
        }
        return state;
    }

    private static async Task<bool> AssetsAvailableAsync(
        ImportState state,
        string catalogRootPath,
        CancellationToken cancellationToken)
    {
        var store = await AssetCatalogStore.OpenAsync(catalogRootPath, cancellationToken);
        foreach (var value in state.AssetIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (store.ResolveBlobPath(AssetId.Parse(value)) is null) return false;
        }
        return true;
    }

    private static async Task WriteStateAsync(
        string path,
        ImportState state,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.partial");
        try
        {
            await using (var stream = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
                await stream.WriteAsync("\n"u8.ToArray(), cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static void ValidateRequest(UyaAssetImportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceIsoPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CatalogRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Revision);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ImporterVersion);
        if (request.Fingerprint.Length != 32 || !request.Fingerprint.All(Uri.IsHexDigit))
            throw new ArgumentException("UYA source fingerprint must be a 32-character MD5 value.", nameof(request));
    }

    private static ValueTask ReportAsync(Func<IsoProgress, ValueTask>? progress, int completed, int total) =>
        progress?.Invoke(new(completed, Math.Max(1, total))) ?? ValueTask.CompletedTask;

    private static ValueTask ReportScaledAsync(
        Func<IsoProgress, ValueTask> progress,
        IsoProgress value,
        int start,
        int end)
    {
        var completed = value.Total <= 0
            ? start
            : start + (int)Math.Min(end - start, value.Completed * (end - start) / value.Total);
        return progress(new(completed, 10_000));
    }

    private static UyaAssetImportResult Result(ImportState state, bool resumed) => new(
        state.CompletedLevels.Count,
        state.CompletedLevels.Count,
        state.AssetAppearances,
        state.AssetIds.Count,
        state.FailedAssets,
        resumed);

    private sealed record ImportState(
        int SchemaVersion,
        string Fingerprint,
        string ImporterVersion,
        List<int> CompletedLevels,
        List<string> AssetIds,
        int AssetAppearances,
        int FailedAssets,
        bool Complete);

    private sealed record LevelAssets(IReadOnlyList<AssetCatalogPut> Assets, int FailedAssets);
}
