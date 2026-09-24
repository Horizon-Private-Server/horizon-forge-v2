using Forge.Host.Domain;
using System.Security.Cryptography;

namespace Forge.Host.Games.UYA;

internal static class UyaIsoPatchJournalStore
{
    public const int SchemaVersion = 1;
    public const string DocumentType = "horizon-forge-uya-iso-patch-journal";
    private const string ManifestFileName = "journal.json";

    public static string PathFor(string developmentIsoPath)
    {
        var path = Path.GetFullPath(developmentIsoPath);
        return Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.forge-patch-journal");
    }

    public static async Task<UyaIsoPatchJournal> CreateAsync(
        UyaDevelopmentIsoPatchPlan plan,
        FileStream target,
        CancellationToken cancellationToken)
    {
        var destination = PathFor(plan.DevelopmentIsoPath);
        if (Directory.Exists(destination))
            throw new InvalidOperationException("The development ISO has an unfinished patch journal; recover it first.");
        var temporary = $"{destination}.{Guid.NewGuid():N}.partial";
        Directory.CreateDirectory(temporary);
        try
        {
            var ranges = new List<UyaIsoPatchJournalRange>(plan.SdkPlan.Ranges.Count);
            for (var index = 0; index < plan.SdkPlan.Ranges.Count; index++)
            {
                var range = plan.SdkPlan.Ranges[index];
                var backup = $"range-{index:D2}.bin";
                await CopyRangeAsync(target, range.Offset, range.Length,
                    Path.Combine(temporary, backup), range.SourceSha256, cancellationToken);
                ranges.Add(new(
                    range.Name,
                    range.Offset,
                    range.Length,
                    range.Alignment,
                    range.SourceSha256,
                    range.OutputSha256,
                    backup));
            }
            var journal = new UyaIsoPatchJournal(
                SchemaVersion,
                DocumentType,
                plan.CleanSourcePath,
                plan.DevelopmentIsoPath,
                plan.CleanIsoLength,
                plan.DevelopmentDevice,
                plan.DevelopmentFile,
                plan.SdkPlan.IsoLength,
                plan.SdkPlan.LevelIndex,
                plan.SdkPlan.HeaderSector,
                plan.SdkPlan.PayloadBaseSector,
                plan.SdkPlan.CapacitySectors,
                plan.SdkPlan.RequiredSectors,
                plan.SdkPlan.SourceLevelWadSha256,
                plan.SdkPlan.OutputLevelWadSha256,
                0,
                false,
                ranges);
            await ForgeProjectPersistence.WriteFileSafelyAsync(
                Path.Combine(temporary, ManifestFileName),
                ForgeProjectPersistence.Serialize(journal),
                cancellationToken);
            Directory.Move(temporary, destination);
            return journal;
        }
        finally
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
        }
    }

    public static async Task<UyaIsoPatchJournal?> LoadAsync(
        string developmentIsoPath,
        CancellationToken cancellationToken = default)
    {
        var root = PathFor(developmentIsoPath);
        if (!Directory.Exists(root)) return null;
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The ISO patch journal cannot be a symbolic link.");
        var journal = ForgeProjectPersistence.Deserialize<UyaIsoPatchJournal>(
            await File.ReadAllBytesAsync(Path.Combine(root, ManifestFileName), cancellationToken),
            "ISO patch journal");
        Validate(journal, developmentIsoPath);
        for (var index = 0; index < journal.Ranges.Count; index++)
        {
            var path = BackupPath(root, journal.Ranges[index].BackupFile);
            var info = new FileInfo(path);
            if (!info.Exists
                || (info.Attributes & FileAttributes.ReparsePoint) != 0
                || info.Length != journal.Ranges[index].Length
                || await HashFileAsync(path, cancellationToken) != journal.Ranges[index].SourceSha256)
                throw new InvalidDataException($"ISO patch journal backup {index} is missing or corrupt.");
        }
        return journal;
    }

    public static async Task<byte[]> ReadBackupAsync(
        string developmentIsoPath,
        UyaIsoPatchJournalRange range,
        CancellationToken cancellationToken) =>
        await File.ReadAllBytesAsync(BackupPath(PathFor(developmentIsoPath), range.BackupFile), cancellationToken);

    public static async Task UpdateAsync(
        UyaIsoPatchJournal journal,
        CancellationToken cancellationToken)
    {
        Validate(journal, journal.DevelopmentIsoPath);
        await ForgeProjectPersistence.WriteFileSafelyAsync(
            Path.Combine(PathFor(journal.DevelopmentIsoPath), ManifestFileName),
            ForgeProjectPersistence.Serialize(journal),
            cancellationToken);
    }

    public static void Delete(string developmentIsoPath)
    {
        var path = PathFor(developmentIsoPath);
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private static void Validate(UyaIsoPatchJournal journal, string developmentIsoPath)
    {
        if (journal.SchemaVersion != SchemaVersion
            || journal.DocumentType != DocumentType
            || !UyaIsoService.PathEquals(journal.DevelopmentIsoPath, UyaIsoService.ResolvePath(developmentIsoPath))
            || !Path.IsPathFullyQualified(journal.CleanSourcePath)
            || journal.CleanIsoLength <= 0
            || journal.DevelopmentDevice == 0
            || journal.DevelopmentFile == 0
            || journal.IsoLength <= 0
            || journal.LevelIndex < 0
            || journal.HeaderSector < 0
            || journal.PayloadBaseSector < 0
            || journal.CapacitySectors <= 0
            || journal.RequiredSectors <= 0
            || journal.RequiredSectors > journal.CapacitySectors
            || !BakeSchema.IsFingerprint(journal.SourceLevelWadSha256)
            || !BakeSchema.IsFingerprint(journal.OutputLevelWadSha256)
            || journal.Ranges.Count == 0
            || journal.CompletedRanges < 0
            || journal.CompletedRanges > journal.Ranges.Count)
            throw new InvalidDataException("ISO patch journal metadata is invalid.");

        long end = 0;
        for (var index = 0; index < journal.Ranges.Count; index++)
        {
            var range = journal.Ranges[index];
            if (string.IsNullOrWhiteSpace(range.Name)
                || range.Offset < end
                || range.Length <= 0
                || range.Alignment <= 0
                || range.Offset % range.Alignment != 0
                || range.Length > journal.IsoLength
                || range.Offset > journal.IsoLength - range.Length
                || !BakeSchema.IsFingerprint(range.SourceSha256)
                || !BakeSchema.IsFingerprint(range.OutputSha256)
                || range.BackupFile != $"range-{index:D2}.bin")
                throw new InvalidDataException("ISO patch journal range metadata is invalid.");
            end = range.Offset + range.Length;
        }
    }

    private static string BackupPath(string root, string name) =>
        ForgeProjectPersistence.ResolveRelativePath(root, name);

    private static async Task CopyRangeAsync(
        FileStream input,
        long offset,
        int length,
        string destination,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        input.Position = offset;
        await using var output = new FileStream(
            destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        var remaining = length;
        while (remaining > 0)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken);
            if (read == 0) throw new EndOfStreamException("The development ISO ended inside a journal range.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }
        await output.FlushAsync(cancellationToken);
        output.Flush(flushToDisk: true);
        var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (actual != expectedSha256) throw new InvalidDataException("The patch preimage changed while creating its journal.");
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }
}
