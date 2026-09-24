using Forge.Host.Domain;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using RatchetPs2.Core.Disc;
using RatchetPs2.Core.Games;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.UYA;

public static partial class UyaIsoService
{
    private const int SectorSize = 2048;
    private const int BufferSize = 1024 * 1024;
    private static DiscImageProfile SupportedProfile => DiscImageInspector.GetSupportedProfile(GameId.UYA);

    public static string SupportedMd5 => SupportedProfile.Md5;
    public static long SupportedSize => SupportedProfile.Size;

    public static async Task<UyaIsoIdentity> ValidateAsync(
        string sourcePath,
        Func<IsoProgress, ValueTask>? progress = null,
        CancellationToken cancellationToken = default,
        string? expectedMd5 = null,
        long? expectedSize = null)
    {
        expectedMd5 ??= SupportedMd5;
        expectedSize ??= SupportedSize;
        var fullPath = Path.GetFullPath(sourcePath);
        await using var source = new FileStream(
            fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var disc = DiscImageInspector.Inspect(GameId.UYA, source);
        var game = disc.Game?.ToString() ?? "Unknown";

        source.Position = 0;
        var fingerprint = await HashAsync(source, progress, cancellationToken);
        var supported = disc.Game == GameId.UYA && disc.Region == SupportedProfile.Region && source.Length == expectedSize
            && fingerprint.Equals(expectedMd5, StringComparison.OrdinalIgnoreCase);
        var diagnostic = supported
            ? "Verified clean UYA NTSC-U disc."
            : $"Unsupported disc: expected clean UYA NTSC-U {SupportedProfile.Serial}, {expectedSize} bytes, MD5 {expectedMd5}; found {disc.Serial} {disc.Region} revision {disc.Revision}, {source.Length} bytes, MD5 {fingerprint}.";
        return new(supported, game, disc.Region, disc.Revision, disc.Serial, source.Length, fingerprint, diagnostic);
    }

    public static UyaIsoIdentity InspectDevelopment(
        Stream source,
        long? expectedSize = null,
        bool allowLarger = false)
    {
        expectedSize ??= SupportedSize;
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || !source.CanSeek)
            throw new ArgumentException("The development ISO stream must be readable and seekable.", nameof(source));
        var disc = DiscImageInspector.Inspect(GameId.UYA, source);
        var game = disc.Game?.ToString() ?? "Unknown";
        var sizeSupported = allowLarger
            ? source.Length >= expectedSize && source.Length % SectorSize == 0
            : source.Length == expectedSize;
        var supported = disc.Game == GameId.UYA
            && disc.Region == SupportedProfile.Region
            && disc.Revision == SupportedProfile.Revision
            && sizeSupported;
        var expectedLength = allowLarger
            ? $"a sector-aligned size of at least {expectedSize} bytes"
            : $"{expectedSize} bytes";
        var diagnostic = supported
            ? "Verified writable UYA NTSC-U 1.00 development image layout."
            : $"Unsupported development disc: expected {SupportedProfile.Serial} NTSC-U revision 1.00, {expectedLength}; "
                + $"found {disc.Serial} {disc.Region} revision {disc.Revision}, {source.Length} bytes.";
        return new(supported, game, disc.Region, disc.Revision, disc.Serial, source.Length, string.Empty, diagnostic);
    }

    public static async Task<DevelopmentIsoResult> CreateDevelopmentCopyAsync(
        string sourcePath,
        string targetPath,
        string expectedFingerprint,
        bool overwrite,
        Func<IsoProgress, ValueTask>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var source = ResolvePath(sourcePath);
        var target = ResolvePath(targetPath);
        if (PathEquals(source, target)) throw new ArgumentException("The clean source and development ISO resolve to the same file.");
        if (!FingerprintPattern().IsMatch(expectedFingerprint)) throw new ArgumentException("The expected source fingerprint is invalid.");
        if (File.Exists(targetPath) && !overwrite) throw new IOException("The development ISO already exists.");

        var targetDirectory = Path.GetDirectoryName(Path.GetFullPath(targetPath))
            ?? throw new ArgumentException("The development ISO must have a parent directory.");
        Directory.CreateDirectory(targetDirectory);
        var partial = Path.Combine(targetDirectory, $".{Path.GetFileName(targetPath)}.forge-partial");
        if (PathEquals(ResolvePath(partial), source)) throw new ArgumentException("The partial-copy path resolves to the clean source ISO.");
        File.Delete(partial);

        var sourceLength = new FileInfo(sourcePath).Length;
        EnsureEnoughSpace(sourceLength, GetAvailableSpace(targetDirectory));

        try
        {
            var copiedFingerprint = await CopyAndHashAsync(sourcePath, partial, sourceLength, progress, cancellationToken);
            if (!copiedFingerprint.Equals(expectedFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The clean source changed after it was validated; select it again.");
            }

            await using (var verification = new FileStream(
                partial, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var verifiedFingerprint = await HashAsync(
                    verification, progress, cancellationToken, sourceLength, checked(sourceLength * 2));
                if (!verifiedFingerprint.Equals(copiedFingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("Development ISO verification failed; the existing destination was not changed.");
                }
            }

            Commit(partial, targetPath);
            return new(Path.GetFullPath(targetPath), sourceLength, copiedFingerprint);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            File.Delete(partial);
            throw;
        }
    }

    public static void EnsureEnoughSpace(long requiredBytes, long availableBytes)
    {
        if (requiredBytes < 0 || availableBytes < 0) throw new ArgumentOutOfRangeException(nameof(requiredBytes));
        if (availableBytes < requiredBytes)
        {
            throw new IOException($"Not enough free space for the development ISO: need {requiredBytes} bytes, have {availableBytes} bytes.");
        }
    }

    internal static long GetAvailableSpace(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        var drive = DriveInfo.GetDrives()
            .Where(candidate => candidate.IsReady && IsPathInside(candidate.RootDirectory.FullName, fullPath))
            .OrderByDescending(candidate => candidate.RootDirectory.FullName.Length)
            .FirstOrDefault();
        return drive?.AvailableFreeSpace
            ?? throw new IOException($"Could not determine free space for {directory}.");
    }

    private static async Task<string> CopyAndHashAsync(
        string sourcePath,
        string partialPath,
        long total,
        Func<IsoProgress, ValueTask>? progress,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var target = new FileStream(
            partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var buffer = GC.AllocateUninitializedArray<byte>(BufferSize);
        long copied = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, cancellationToken)) != 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            hash.AppendData(buffer, 0, count);
            copied += count;
            if (progress is not null) await progress(new(copied, checked(total * 2)));
        }
        await target.FlushAsync(cancellationToken);
        target.Flush(flushToDisk: true);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task<string> HashAsync(
        Stream source,
        Func<IsoProgress, ValueTask>? progress,
        CancellationToken cancellationToken,
        long completedBase = 0,
        long? progressTotal = null)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var buffer = GC.AllocateUninitializedArray<byte>(BufferSize);
        long completed = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, cancellationToken)) != 0)
        {
            hash.AppendData(buffer, 0, count);
            completed += count;
            if (progress is not null) await progress(new(completedBase + completed, progressTotal ?? source.Length));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    internal static void Commit(string partial, string target)
    {
        File.Move(partial, target, overwrite: true);
    }

    internal static string ResolvePath(string value)
    {
        var info = new FileInfo(Path.GetFullPath(value));
        return Path.GetFullPath(info.Exists ? info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? info.FullName : info.FullName);
    }

    internal static bool PathEquals(string left, string right) => string.Equals(
        left, right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsPathInside(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative != ".."
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }

    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.IgnoreCase)]
    private static partial Regex FingerprintPattern();
}
