using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RatchetPs2.Core.Games;
using RatchetPs2.Sdk;

namespace Forge.Host.Domain;

public static class AssetImportService
{
    private const int StateSchemaVersion = 0;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static async Task<AssetImportResult> ImportAsync(
        AssetImportRequest request,
        Func<byte[], int, CatalogLevelAssets> readAssets,
        Func<IsoProgress, ValueTask>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        ArgumentNullException.ThrowIfNull(readAssets);
        var statePath = GetStatePath(request);
        var state = await ReadStateAsync(statePath, request, cancellationToken);
        if (state?.Complete == true && !request.Force
            && await AssetsAvailableAsync(state, request.CatalogRootPath, cancellationToken))
            return Result(state, resumed: true);

        var identity = await IsoSourceService.ValidateAsync(
            request.Game, request.SourceIsoPath,
            progress is null ? null : value => ReportScaledAsync(progress, value, 0, 2_000),
            cancellationToken);
        if (!identity.IsSupported || !identity.Fingerprint.Equals(request.Fingerprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The {request.Game} source ISO no longer matches its verified fingerprint.");

        await using var iso = new FileStream(
            request.SourceIsoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        return await ImportValidatedAsync(
            iso,
            request,
            readAssets,
            progress is null ? null : value => ReportScaledAsync(progress, value, 2_000, 10_000),
            cancellationToken);
    }

    public static async Task<AssetImportResult> ImportValidatedAsync(
        Stream iso,
        AssetImportRequest request,
        Func<byte[], int, CatalogLevelAssets> readAssets,
        Func<IsoProgress, ValueTask>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        ArgumentNullException.ThrowIfNull(readAssets);
        ArgumentNullException.ThrowIfNull(iso);
        if (!iso.CanRead || !iso.CanSeek)
            throw new ArgumentException("The ISO stream must be readable and seekable.", nameof(iso));

        var statePath = GetStatePath(request);
        var state = await ReadStateAsync(statePath, request, cancellationToken);
        if (request.Force && state?.Complete == true) state = null;
        else if (state is not null
            && !await AssetsAvailableAsync(state, request.CatalogRootPath, cancellationToken)) state = null;
        state ??= new ImportState(StateSchemaVersion, request.Fingerprint, request.ImporterVersion, [], [], 0, 0, false);
        if (state.Complete) return Result(state, resumed: true);

        var resumed = state.CompletedLevels.Count > 0;
        var levels = LevelCatalogReader.FindAvailable(request.Game, iso);
        var completed = state.CompletedLevels.ToHashSet();
        if (completed.Any(level => !levels.Contains(level)))
            throw new InvalidDataException("Asset import state contains levels not present in the source ISO.");
        var assetIds = state.AssetIds.ToHashSet(StringComparer.Ordinal);
        var appearances = state.AssetAppearances;
        var failedAssets = state.FailedAssets;
        var store = await AssetCatalogStore.OpenAsync(request.CatalogRootPath, cancellationToken);

        await ReportAsync(progress, completed.Count, levels.Count);
        foreach (var level in levels)
        {
            if (completed.Contains(level)) continue;
            cancellationToken.ThrowIfCancellationRequested();

            var levelWad = LevelArchiveReader.ExtractPrimary(request.Game, iso, level);
            var levelAssets = readAssets(levelWad, level);
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
            throw new InvalidDataException("The ISO does not contain any level WADs.");
        return Result(state, resumed);
    }

    private static string GetStatePath(AssetImportRequest request)
    {
        var versionHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.ImporterVersion)))
            .ToLowerInvariant();
        return Path.Combine(request.CatalogRootPath, "imports", request.Game.ToString().ToLowerInvariant(), request.Fingerprint, $"{versionHash}.json");
    }

    private static async Task<ImportState?> ReadStateAsync(
        string path,
        AssetImportRequest request,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        ImportState state;
        try
        {
            state = await JsonSerializer.DeserializeAsync<ImportState>(stream, JsonOptions, cancellationToken)
                ?? throw new InvalidDataException("Asset import state is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Asset import state contains invalid JSON.", exception);
        }
        if (state.SchemaVersion != StateSchemaVersion
            || state.Fingerprint != request.Fingerprint
            || state.ImporterVersion != request.ImporterVersion)
            throw new InvalidDataException("Asset import state does not match this importer.");
        if (state.CompletedLevels is null || state.AssetIds is null || state.AssetAppearances < 0 || state.FailedAssets < 0
            || state.CompletedLevels.Count != state.CompletedLevels.Distinct().Count()
            || state.CompletedLevels.Any(level => level < 0))
            throw new InvalidDataException("Asset import state contains invalid progress data.");
        try
        {
            foreach (var id in state.AssetIds) AssetId.Parse(id);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            throw new InvalidDataException("Asset import state contains an invalid Asset ID.", exception);
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
                FileOptions.Asynchronous))
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

    internal static void ValidateRequest(AssetImportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Game is not (GameId.UYA or GameId.DL)) throw new ArgumentException("Unsupported asset source game.");
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceIsoPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CatalogRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Revision);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ImporterVersion);
        if (request.Fingerprint is null || request.Fingerprint.Length != 32 || !request.Fingerprint.All(Uri.IsHexDigit))
            throw new ArgumentException("Source fingerprint must be a 32-character MD5 value.", nameof(request));
    }

    internal static ValueTask ReportAsync(Func<IsoProgress, ValueTask>? progress, int completed, int total) =>
        progress?.Invoke(new(completed, Math.Max(1, total))) ?? ValueTask.CompletedTask;

    internal static ValueTask ReportScaledAsync(
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

    private static AssetImportResult Result(ImportState state, bool resumed) => new(
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

}
