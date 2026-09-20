using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Forge.Host.Domain;

public static partial class UyaIsoService
{
    private const int SectorSize = 2048;
    private const string SupportedSerial = "SCUS-97353";
    // PCSX2 RedumpDatabase entry for the single 2048-byte-sector UYA DVD data track.
    public const string SupportedMd5 = "ba9f2b38c7346e7b6e5b8e87717d5893";
    public const long SupportedSize = 4_379_377_664;
    private const int BufferSize = 1024 * 1024;

    public static async Task<UyaIsoIdentity> ValidateAsync(
        string sourcePath,
        Func<IsoProgress, ValueTask>? progress = null,
        CancellationToken cancellationToken = default,
        string expectedMd5 = SupportedMd5,
        long expectedSize = SupportedSize)
    {
        var fullPath = Path.GetFullPath(sourcePath);
        await using var source = new FileStream(
            fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var system = ReadSystemConfiguration(source);
        var serial = ParseValue(system, BootPattern(), "BOOT2 serial").Replace('_', '-').Replace(".", "");
        var revision = ParseValue(system, VersionPattern(), "VER");
        var videoMode = ParseValue(system, VideoModePattern(), "VMODE");
        var region = videoMode.Equals("NTSC", StringComparison.OrdinalIgnoreCase) ? "NTSC-U" : videoMode.ToUpperInvariant();
        var game = serial.Equals(SupportedSerial, StringComparison.OrdinalIgnoreCase) ? "UYA" : "Unknown";

        source.Position = 0;
        var fingerprint = await HashAsync(source, progress, cancellationToken);
        var supported = game == "UYA" && region == "NTSC-U" && source.Length == expectedSize
            && fingerprint.Equals(expectedMd5, StringComparison.OrdinalIgnoreCase);
        var diagnostic = supported
            ? "Verified clean UYA NTSC-U disc."
            : $"Unsupported disc: expected clean UYA NTSC-U {SupportedSerial}, {expectedSize} bytes, MD5 {expectedMd5}; found {serial} {region} revision {revision}, {source.Length} bytes, MD5 {fingerprint}.";
        return new(supported, game, region, revision, serial, source.Length, fingerprint, diagnostic);
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

    private static long GetAvailableSpace(string directory)
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

    private static string ReadSystemConfiguration(Stream source)
    {
        Span<byte> descriptor = stackalloc byte[SectorSize];
        source.Position = 16L * SectorSize;
        source.ReadExactly(descriptor);
        if (descriptor[0] != 1 || !descriptor[1..6].SequenceEqual("CD001"u8))
        {
            throw new InvalidDataException("The selected file is not an ISO 9660 disc image.");
        }

        var root = descriptor[156..];
        var rootSector = BinaryPrimitives.ReadUInt32LittleEndian(root[2..]);
        var rootLength = BinaryPrimitives.ReadUInt32LittleEndian(root[10..]);
        if (rootLength is 0 or > 16 * 1024 * 1024) throw new InvalidDataException("ISO root directory is invalid.");
        var directory = GC.AllocateUninitializedArray<byte>((int)rootLength);
        source.Position = (long)rootSector * SectorSize;
        source.ReadExactly(directory);

        for (var offset = 0; offset < directory.Length;)
        {
            var recordLength = directory[offset];
            if (recordLength == 0)
            {
                offset = ((offset / SectorSize) + 1) * SectorSize;
                continue;
            }
            if (offset + recordLength > directory.Length || recordLength < 34) break;
            var record = directory.AsSpan(offset, recordLength);
            var nameLength = record[32];
            if (33 + nameLength <= record.Length)
            {
                var name = Encoding.ASCII.GetString(record.Slice(33, nameLength));
                if (name.Equals("SYSTEM.CNF;1", StringComparison.OrdinalIgnoreCase))
                {
                    var sector = BinaryPrimitives.ReadUInt32LittleEndian(record[2..]);
                    var length = BinaryPrimitives.ReadUInt32LittleEndian(record[10..]);
                    if (length > 64 * 1024) throw new InvalidDataException("SYSTEM.CNF is unexpectedly large.");
                    var bytes = GC.AllocateUninitializedArray<byte>((int)length);
                    source.Position = (long)sector * SectorSize;
                    source.ReadExactly(bytes);
                    return Encoding.ASCII.GetString(bytes);
                }
            }
            offset += recordLength;
        }
        throw new InvalidDataException("ISO does not contain SYSTEM.CNF.");
    }

    private static string ParseValue(string configuration, Regex pattern, string name)
    {
        var match = pattern.Match(configuration);
        return match.Success ? match.Groups[1].Value : throw new InvalidDataException($"SYSTEM.CNF does not contain {name}.");
    }

    private static void Commit(string partial, string target)
    {
        File.Move(partial, target, overwrite: true);
    }

    private static string ResolvePath(string value)
    {
        var info = new FileInfo(Path.GetFullPath(value));
        return Path.GetFullPath(info.Exists ? info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? info.FullName : info.FullName);
    }

    private static bool PathEquals(string left, string right) => string.Equals(
        left, right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsPathInside(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative != ".."
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }

    [GeneratedRegex(@"BOOT2\s*=\s*cdrom0:\\([A-Z]{4}_[0-9]{3}\.[0-9]{2});1", RegexOptions.IgnoreCase)]
    private static partial Regex BootPattern();

    [GeneratedRegex(@"^\s*VER\s*=\s*([^\s\r\n]+)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^\s*VMODE\s*=\s*([^\s\r\n]+)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex VideoModePattern();

    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.IgnoreCase)]
    private static partial Regex FingerprintPattern();
}
