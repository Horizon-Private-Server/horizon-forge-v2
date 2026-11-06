using System.Security.Cryptography;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.LevelAssets;
using RatchetPs2.Sdk;

namespace Forge.Host.Domain;

internal sealed record ContentImportSource(
    IsoIdentity Identity, string LevelHash, IReadOnlyList<LevelContentFamily> Families,
    byte[]? LevelWad = null, LevelContentConversion? Conversion = null);

internal static class ContentImportSourceCache
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Dictionary<GameId, (DiscVersion Version, IsoIdentity Identity)> Identities = new();
    private static (DiscVersion Version, GameId SourceGame, GameId TargetGame, int Level, ContentImportSource Source)? _source;

    public static async Task<ContentImportSource> ReadAsync(GameId sourceGame, GameId targetGame,
        string path, string fingerprint, int level, string region, string revision,
        Func<IsoProgress, ValueTask>? progress, CancellationToken token)
    {
        if (!Enum.IsDefined(sourceGame) || !Enum.IsDefined(targetGame)) throw new ArgumentOutOfRangeException(nameof(sourceGame));
        var version = Version(path);
        await Gate.WaitAsync(token);
        try
        {
            if (_source is { } cached && cached.Version == version && cached.SourceGame == sourceGame
                && cached.TargetGame == targetGame && cached.Level == level)
            {
                ValidateIdentity(cached.Source.Identity, sourceGame, region, revision, fingerprint);
                if (progress is not null) await progress(new(9_000, 10_000));
                return cached.Source;
            }
            var identity = Identities.TryGetValue(sourceGame, out var verified) && verified.Version == version
                ? verified.Identity
                : await IsoSourceService.ValidateAsync(sourceGame, path,
                    progress is null ? null : value => progress(new(value.Total == 0 ? 0 : value.Completed * 7_000 / value.Total, 10_000)), token);
            ValidateIdentity(identity, sourceGame, region, revision, fingerprint);
            if (version != Version(path)) throw new IOException("The donor ISO changed during verification; retry the preview.");
            Identities[sourceGame] = (version, identity);
            if (progress is not null) await progress(new(7_500, 10_000));
            var source = await Task.Run(() =>
            {
                using var iso = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.RandomAccess);
                token.ThrowIfCancellationRequested();
                var wad = LevelArchiveReader.ExtractPrimary(sourceGame, iso, level);
                var conversion = LevelContentConverter.Convert(sourceGame, targetGame, wad, token);
                var families = LevelContentInventoryReader.ReadConverted(sourceGame, targetGame, conversion, token);
                return new ContentImportSource(identity, Convert.ToHexString(SHA256.HashData(wad)).ToLowerInvariant(), families, wad, conversion);
            }, token);
            token.ThrowIfCancellationRequested();
            if (version != Version(path)) throw new IOException("The donor ISO changed while reading its content; retry the preview.");
            _source = (version, sourceGame, targetGame, level, source);
            if (progress is not null) await progress(new(9_000, 10_000));
            return source;
        }
        finally { Gate.Release(); }
    }

    internal static void ValidateIdentity(IsoIdentity identity, GameId game, string region, string revision, string fingerprint)
    {
        if (!identity.IsSupported || identity.Game != game.ToString() || identity.Region != region
            || identity.Revision != revision || !identity.Fingerprint.Equals(fingerprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The donor ISO does not match the verified {game} {region} {revision} source.");
    }

    private static DiscVersion Version(string path)
    {
        var file = new FileInfo(Path.GetFullPath(path));
        if (!file.Exists) throw new FileNotFoundException("The donor ISO does not exist.", file.FullName);
        return new(file.FullName, file.Length, file.LastWriteTimeUtc.Ticks);
    }
    private sealed record DiscVersion(string Path, long Length, long LastWriteTicks);
}
