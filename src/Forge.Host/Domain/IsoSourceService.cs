using System.Security.Cryptography;
using RatchetPs2.Core.Games;
using RatchetPs2.Sdk;

namespace Forge.Host.Domain;

public static class IsoSourceService
{
    private const int BufferSize = 1024 * 1024;

    public static async Task<IsoIdentity> ValidateAsync(
        GameId game,
        string sourcePath,
        Func<IsoProgress, ValueTask>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await using var source = new FileStream(
            Path.GetFullPath(sourcePath), FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ValidateAsync(game, source, progress, cancellationToken);
    }

    internal static async Task<IsoIdentity> ValidateAsync(
        GameId game,
        FileStream source,
        Func<IsoProgress, ValueTask>? progress = null,
        CancellationToken cancellationToken = default,
        string? expectedMd5 = null,
        long? expectedSize = null)
    {
        var profile = DiscImageInspector.GetSupportedProfile(game);
        expectedMd5 ??= profile.Md5;
        expectedSize ??= profile.Size;
        var disc = DiscImageInspector.Inspect(game, source);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var buffer = GC.AllocateUninitializedArray<byte>(BufferSize);
        await Task.Yield();
        long completed = 0;
        int count;
        while ((count = await RandomAccess.ReadAsync(source.SafeFileHandle, buffer, completed, cancellationToken)) != 0)
        {
            hash.AppendData(buffer, 0, count);
            completed += count;
            if (progress is not null) await progress(new(completed, source.Length));
        }
        var fingerprint = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        var supported = disc.Game == game && disc.Region == profile.Region && disc.Revision == profile.Revision
            && source.Length == expectedSize && fingerprint.Equals(expectedMd5, StringComparison.OrdinalIgnoreCase);
        var diagnostic = supported
            ? $"Verified clean {game} {profile.Region} disc."
            : $"Unsupported disc: expected clean {game} {profile.Region} {profile.Serial}, {expectedSize} bytes, MD5 {expectedMd5}; "
                + $"found {disc.Serial} {disc.Region} revision {disc.Revision}, {source.Length} bytes, MD5 {fingerprint}.";
        return new(supported, disc.Game?.ToString() ?? "Unknown", disc.Region, disc.Revision, disc.Serial,
            source.Length, fingerprint, diagnostic);
    }
}
