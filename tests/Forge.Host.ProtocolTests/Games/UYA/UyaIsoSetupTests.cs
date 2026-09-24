using Forge.Host.Games.UYA;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using Forge.Host.Domain;
using RatchetPs2.Games.UYA.Level;

namespace Forge.Host.ProtocolTests.Games.UYA;

internal static class UyaIsoSetupTests
{
    public static async Task RunAsync()
    {
        const int levelWadSector = 1200;
        Equal("ba9f2b38c7346e7b6e5b8e87717d5893", UyaIsoService.SupportedMd5, "PCSX2 UYA MD5");
        Equal(4_379_377_664, UyaIsoService.SupportedSize, "PCSX2 UYA track size");
        var directory = Path.Combine(Path.GetTempPath(), $"forge-iso-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "clean.iso");
            var sourceBytes = CreateSyntheticIso("SCUS_973.53", "NTSC");
            await File.WriteAllBytesAsync(source, sourceBytes);
            var md5 = Convert.ToHexString(MD5.HashData(sourceBytes)).ToLowerInvariant();

            var identity = await UyaIsoService.ValidateAsync(
                source, expectedMd5: md5, expectedSize: sourceBytes.Length);
            Equal(true, identity.IsSupported, "synthetic UYA identity");
            Equal("SCUS-97353", identity.Serial, "synthetic UYA serial");
            Equal("NTSC-U", identity.Region, "synthetic UYA region");
            Equal(md5, identity.Fingerprint, "synthetic UYA MD5");

            var wrong = Path.Combine(directory, "wrong.iso");
            var wrongBytes = CreateSyntheticIso("SCES_524.56", "PAL");
            await File.WriteAllBytesAsync(wrong, wrongBytes);
            var wrongMd5 = Convert.ToHexString(MD5.HashData(wrongBytes)).ToLowerInvariant();
            var wrongIdentity = await UyaIsoService.ValidateAsync(
                wrong, expectedMd5: wrongMd5, expectedSize: wrongBytes.Length);
            Equal(false, wrongIdentity.IsSupported, "wrong disc rejection");
            Contains(wrongIdentity.Diagnostic, "SCES-52456", "wrong disc diagnostic serial");
            Contains(wrongIdentity.Diagnostic, "PAL", "wrong disc diagnostic region");

            await ExpectAsync<ArgumentException>(() => UyaIsoService.CreateDevelopmentCopyAsync(
                source, source, md5, overwrite: true));
            Expect<IOException>(() => UyaIsoService.EnsureEnoughSpace(10, 9));

            var target = Path.Combine(directory, "development.iso");
            var cancellation = new CancellationTokenSource();
            await ExpectAsync<OperationCanceledException>(() => UyaIsoService.CreateDevelopmentCopyAsync(
                source,
                target,
                md5,
                overwrite: false,
                progress =>
                {
                    if (progress.Completed > 0) cancellation.Cancel();
                    return ValueTask.CompletedTask;
                },
                cancellation.Token));
            Equal(false, File.Exists(target), "cancelled copy final path");
            Equal(true, File.Exists(Path.Combine(directory, ".development.iso.forge-partial")), "cancelled partial marker");

            var result = await UyaIsoService.CreateDevelopmentCopyAsync(source, target, md5, overwrite: false);
            Equal(md5, result.Fingerprint, "development ISO MD5");
            Equal(md5, await HashFileAsync(target), "development ISO bytes");

            using var sourceStream = new MemoryStream(sourceBytes, writable: false);
            var packedLevel = UyaLooseLevelWadExtractor.ExtractPrimary(sourceStream, 3).Bytes;
            packedLevel[0x0c] = 1;
            var plan = await UyaIsoPatchService.PlanAsync(
                source, target, 3, packedLevel, expectedIsoSize: sourceBytes.Length);
            Equal(true, plan.DevelopmentIso.IsSupported, "development ISO structural identity");
            Equal(true, plan.SdkPlan.FitsInPlace, "development ISO patch capacity");
            Equal(1, plan.SdkPlan.Ranges.Count, "header-only synthetic patch range count");
            Equal(levelWadSector * (long)UyaLevelConstants.SectorSize,
                plan.SdkPlan.Ranges.Single().Offset, "development ISO patch header offset");

            await using (var locked = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var lockedError = await ExpectAsync<IOException>(() => UyaIsoPatchService.PlanAsync(
                    source, target, 3, packedLevel, expectedIsoSize: sourceBytes.Length));
                Contains(lockedError.Message, "Close/eject it from PCSX2 and retry.",
                    "locked development ISO guidance");
            }

            var oversizedLevel = packedLevel.Concat(new byte[UyaLevelConstants.SectorSize]).ToArray();
            BinaryPrimitives.WriteInt32LittleEndian(oversizedLevel.AsSpan(0x10), 1);
            BinaryPrimitives.WriteInt32LittleEndian(oversizedLevel.AsSpan(0x14), 1);
            oversizedLevel[UyaLevelConstants.SectorSize] = 0x5a;
            var fallback = await UyaIsoPatchService.PlanAsync(
                source, target, 3, oversizedLevel, expectedIsoSize: sourceBytes.Length);
            Equal(false, fallback.SdkPlan.FitsInPlace, "oversized level requires full-image fallback");
            Equal(0, fallback.SdkPlan.Ranges.Count, "fallback plan publishes no unsafe in-place ranges");
            Equal(true, fallback.SdkPlan.Replacement is not null, "fallback replacement layout");

            await ExpectAsync<ArgumentException>(() => UyaIsoPatchService.PlanAsync(
                source, source, 3, packedLevel, expectedIsoSize: sourceBytes.Length));
            var alias = Path.Combine(directory, "clean-alias.iso");
            CreateHardLink(alias, source);
            await ExpectAsync<ArgumentException>(() => UyaIsoPatchService.PlanAsync(
                source, alias, 3, packedLevel, expectedIsoSize: sourceBytes.Length));
            File.Delete(alias);
            await ExpectAsync<InvalidDataException>(() => UyaIsoPatchService.PlanAsync(
                source, wrong, 3, packedLevel, expectedIsoSize: sourceBytes.Length));

            var corrupt = Path.Combine(directory, "corrupt.iso");
            var corruptBytes = sourceBytes.ToArray();
            BinaryPrimitives.WriteInt32LittleEndian(
                corruptBytes.AsSpan(levelWadSector * UyaLevelConstants.SectorSize + 4), int.MaxValue);
            await File.WriteAllBytesAsync(corrupt, corruptBytes);
            await ExpectAsync<InvalidDataException>(() => UyaIsoPatchService.PlanAsync(
                source, corrupt, 3, packedLevel, expectedIsoSize: sourceBytes.Length));

            var unwritable = Path.Combine(directory, "unwritable.iso");
            await File.WriteAllBytesAsync(unwritable, sourceBytes);
            if (OperatingSystem.IsWindows()) File.SetAttributes(unwritable, FileAttributes.ReadOnly);
            else File.SetUnixFileMode(unwritable,
                UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            try
            {
                await ExpectAsync<IOException>(() => UyaIsoPatchService.PlanAsync(
                    source, unwritable, 3, packedLevel, expectedIsoSize: sourceBytes.Length));
            }
            finally
            {
                if (OperatingSystem.IsWindows()) File.SetAttributes(unwritable, FileAttributes.Normal);
                else File.SetUnixFileMode(unwritable, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            await VerifyJournaledPatchingAsync(source, target, sourceBytes, packedLevel, md5);
            await VerifyFullImageReplacementAsync(source, target, sourceBytes, packedLevel, oversizedLevel, md5);

            await File.WriteAllTextAsync(target, "last-known-good");
            await ExpectAsync<InvalidDataException>(() => UyaIsoService.CreateDevelopmentCopyAsync(
                source, target, new string('0', 32), overwrite: true));
            Equal("last-known-good", await File.ReadAllTextAsync(target), "failed overwrite preservation");

            File.Delete(target);
            CreateHardLink(target, source);
            await UyaIsoService.CreateDevelopmentCopyAsync(source, target, md5, overwrite: true);
            await File.AppendAllTextAsync(target, "independent");
            Equal(sourceBytes.Length, new FileInfo(source).Length, "hard-link source isolation");
            Equal(md5, await HashFileAsync(source), "source remains byte-identical");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task VerifyJournaledPatchingAsync(
        string source,
        string target,
        byte[] sourceBytes,
        byte[] packedLevel,
        string sourceMd5)
    {
        var plan = await UyaIsoPatchService.PlanAsync(
            source, target, 3, packedLevel, expectedIsoSize: sourceBytes.Length);
        var result = await UyaIsoPatchService.ApplyAsync(plan);
        Equal(1, result.WrittenRanges, "journaled patch range count");
        Equal(UyaIsoPatchMode.InPlace, result.Mode, "journaled patch mode");
        Equal(false, Directory.Exists(UyaIsoPatchJournalStore.PathFor(target)), "completed patch journal cleanup");
        await using (var stream = File.OpenRead(target))
            Equal(
                Convert.ToHexString(SHA256.HashData(packedLevel)).ToLowerInvariant(),
                Convert.ToHexString(SHA256.HashData(UyaLooseLevelWadExtractor.ExtractPrimary(stream, 3).Bytes))
                    .ToLowerInvariant(),
                "journaled patch payload");
        Equal(sourceMd5, await HashFileAsync(source), "clean source after successful patch");

        foreach (var phase in new[]
        {
            UyaIsoPatchFaultPhase.JournalDurable,
            UyaIsoPatchFaultPhase.RangeDurable,
            UyaIsoPatchFaultPhase.CommitAdvanced,
            UyaIsoPatchFaultPhase.OutputVerified,
        })
        {
            File.Copy(source, target, overwrite: true);
            plan = await UyaIsoPatchService.PlanAsync(
                source, target, 3, packedLevel, expectedIsoSize: sourceBytes.Length);
            await ExpectAsync<TestPatchFaultException>(() => UyaIsoPatchService.ApplyAsync(
                plan,
                progress: null,
                fault =>
                {
                    if (fault.Phase == phase) throw new TestPatchFaultException();
                },
                CancellationToken.None));
            var recovery = await UyaIsoPatchService.InspectRecoveryAsync(target)
                ?? throw new InvalidOperationException($"Missing recovery state after {phase} fault.");
            Equal(
                phase == UyaIsoPatchFaultPhase.JournalDurable
                    ? UyaIsoPatchRecoveryState.Original
                    : UyaIsoPatchRecoveryState.Patched,
                recovery.State,
                $"{phase} recovery state");
            if (phase == UyaIsoPatchFaultPhase.OutputVerified)
                await UyaIsoPatchService.AcceptPatchedRecoveryAsync(target);
            else
                await UyaIsoPatchService.RecoverAsync(target);
            Equal<UyaIsoPatchRecovery?>(null, await UyaIsoPatchService.InspectRecoveryAsync(target),
                $"{phase} recovery cleanup");
            Equal(sourceMd5, await HashFileAsync(source), $"clean source after {phase} fault");
        }

        File.Copy(source, target, overwrite: true);
        plan = await UyaIsoPatchService.PlanAsync(
            source, target, 3, packedLevel, expectedIsoSize: sourceBytes.Length);
        using var cancellation = new CancellationTokenSource();
        await ExpectAsync<OperationCanceledException>(() => UyaIsoPatchService.ApplyAsync(
            plan,
            progress: null,
            fault =>
            {
                if (fault.Phase == UyaIsoPatchFaultPhase.JournalDurable) cancellation.Cancel();
            },
            cancellation.Token));
        Equal(UyaIsoPatchRecoveryState.Original,
            (await UyaIsoPatchService.InspectRecoveryAsync(target))?.State,
            "cancelled patch recovery state");
        await UyaIsoPatchService.RecoverAsync(target);
        Equal(sourceMd5, await HashFileAsync(target), "cancelled patch restoration");
        Equal(sourceMd5, await HashFileAsync(source), "clean source after cancelled patch");
    }

    private static async Task VerifyFullImageReplacementAsync(
        string source,
        string target,
        byte[] sourceBytes,
        byte[] packedLevel,
        byte[] oversizedLevel,
        string sourceMd5)
    {
        var originalTargetMd5 = sourceMd5;
        var temporary = Path.Combine(
            Path.GetDirectoryName(target)!, $".{Path.GetFileName(target)}.forge-replacement");

        File.Copy(source, target, overwrite: true);
        var plan = await UyaIsoPatchService.PlanAsync(
            source, target, 3, oversizedLevel, expectedIsoSize: sourceBytes.Length);
        var replacement = plan.SdkPlan.Replacement!;
        await ExpectAsync<IOException>(() => UyaIsoPatchService.ApplyAsync(
            plan, progress: null, fault: null, CancellationToken.None, replacement.OutputIsoLength - 1));
        Equal(originalTargetMd5, await HashFileAsync(target), "insufficient-space target preservation");

        File.Copy(source, target, overwrite: true);
        plan = await UyaIsoPatchService.PlanAsync(
            source, target, 3, oversizedLevel, expectedIsoSize: sourceBytes.Length);
        using (var cancellation = new CancellationTokenSource())
        {
            await ExpectAsync<OperationCanceledException>(() => UyaIsoPatchService.ApplyAsync(
                plan,
                progress =>
                {
                    if (progress.Phase == "copy" && progress.Completed > 0) cancellation.Cancel();
                    return ValueTask.CompletedTask;
                },
                fault: null,
                cancellation.Token,
                availableBytes: long.MaxValue));
        }
        Equal(originalTargetMd5, await HashFileAsync(target), "interrupted replacement target preservation");
        Equal(false, File.Exists(temporary), "interrupted replacement cleanup");

        File.Copy(source, target, overwrite: true);
        plan = await UyaIsoPatchService.PlanAsync(
            source, target, 3, oversizedLevel, expectedIsoSize: sourceBytes.Length);
        await ExpectAsync<IOException>(() => UyaIsoPatchService.ApplyAsync(
            plan,
            progress: null,
            fault =>
            {
                if (fault.Phase != UyaIsoPatchFaultPhase.ReplacementBuilt) return;
                using var replacementFile = new FileStream(
                    fault.ReplacementPath!, FileMode.Open, FileAccess.Write, FileShare.None);
                replacementFile.Position = (plan.SdkPlan.Replacement!.HeaderSector + 1L)
                    * UyaLevelConstants.SectorSize;
                replacementFile.WriteByte(0xff);
            },
            CancellationToken.None,
            availableBytes: long.MaxValue));
        Equal(originalTargetMd5, await HashFileAsync(target), "failed verification target preservation");
        Equal(false, File.Exists(temporary), "failed verification replacement cleanup");

        File.Copy(source, target, overwrite: true);
        plan = await UyaIsoPatchService.PlanAsync(
            source, target, 3, oversizedLevel, expectedIsoSize: sourceBytes.Length);
        var result = await UyaIsoPatchService.ApplyAsync(
            plan, cancellationToken: CancellationToken.None);
        Equal(UyaIsoPatchMode.FullImageReplacement, result.Mode, "automatic fallback mode");
        Equal(replacement.OutputIsoLength, new FileInfo(target).Length, "replacement ISO length");
        await using (var installed = File.OpenRead(target))
            Equal(
                plan.SdkPlan.OutputLevelWadSha256,
                Convert.ToHexString(SHA256.HashData(
                    UyaLooseLevelWadExtractor.ExtractPrimary(installed, 3).Bytes)).ToLowerInvariant(),
                "replacement installed level");
        var expandedPlan = await UyaIsoPatchService.PlanAsync(
            source,
            target,
            3,
            oversizedLevel,
            expectedIsoSize: sourceBytes.Length);
        Equal(true, expandedPlan.SdkPlan.FitsInPlace, "expanded development ISO can be replanned");
        var retailPlan = await UyaIsoPatchService.PlanAsync(
            source,
            target,
            3,
            packedLevel,
            expectedIsoSize: sourceBytes.Length);
        Equal(true, retailPlan.SdkPlan.Ranges.Any(value => value.Name == "level-info"),
            "compact patch restores the retail level table");
        result = await UyaIsoPatchService.ApplyAsync(retailPlan);
        Equal(UyaIsoPatchMode.InPlace, result.Mode, "compact patch returns to in-place mode");
        await using (var installed = File.OpenRead(target))
            Equal(1200, UyaLevelInfoReader.ReadEntry(installed, 3).LevelWad.Offset,
                "compact patch restores the retail level sector");

        File.Copy(source, target, overwrite: true);
        var forced = await UyaIsoPatchService.PlanAsync(
            source,
            target,
            3,
            packedLevel,
            expectedIsoSize: sourceBytes.Length,
            forceFullImage: true);
        result = await UyaIsoPatchService.ApplyAsync(forced);
        Equal(UyaIsoPatchMode.FullImageReplacement, result.Mode, "forced fallback mode");
        Contains(result.StrategyReason, "explicitly", "forced fallback explanation");
        Equal(sourceMd5, await HashFileAsync(source), "clean source after full-image replacements");
    }

    private static byte[] CreateSyntheticIso(string serial, string videoMode)
    {
        const int levelWadSector = 1200;
        var bytes = new byte[1300 * UyaLevelConstants.SectorSize];

        AddDiscIdentity(bytes, serial, videoMode);

        var levelEntry = bytes.AsSpan(
            UyaLevelConstants.RetailLevelInfoTableOffset + (3 * UyaLevelConstants.LevelInfoSize),
            UyaLevelConstants.LevelInfoSize);
        BinaryPrimitives.WriteInt32LittleEndian(levelEntry[8..], levelWadSector);
        BinaryPrimitives.WriteInt32LittleEndian(levelEntry[12..], 1);
        var wadHeader = bytes.AsSpan(levelWadSector * UyaLevelConstants.SectorSize, UyaLevelConstants.LevelWadHeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(wadHeader, UyaLevelConstants.LevelWadHeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(wadHeader[4..], levelWadSector + 1);
        BinaryPrimitives.WriteInt32LittleEndian(wadHeader[8..], 3);
        return bytes;
    }

    internal static void AddDiscIdentity(byte[] bytes, string serial = "SCUS_973.53", string videoMode = "NTSC")
    {
        const int rootSector = 20;
        const int systemSector = 21;

        var descriptor = bytes.AsSpan(16 * UyaLevelConstants.SectorSize, UyaLevelConstants.SectorSize);
        descriptor[0] = 1;
        "CD001"u8.CopyTo(descriptor[1..]);
        descriptor[6] = 1;
        WriteDirectoryRecord(descriptor[156..], rootSector, UyaLevelConstants.SectorSize, [0]);

        var root = bytes.AsSpan(rootSector * UyaLevelConstants.SectorSize, UyaLevelConstants.SectorSize);
        WriteDirectoryRecord(root, systemSector, 128, Encoding.ASCII.GetBytes("SYSTEM.CNF;1"));
        var configuration = Encoding.ASCII.GetBytes($"BOOT2 = cdrom0:\\{serial};1\r\nVER = 1.00\r\nVMODE = {videoMode}\r\n");
        configuration.CopyTo(bytes.AsSpan(systemSector * UyaLevelConstants.SectorSize));
    }

    private static void WriteDirectoryRecord(Span<byte> destination, int sector, int length, ReadOnlySpan<byte> name)
    {
        var recordLength = 33 + name.Length + (name.Length % 2 == 0 ? 1 : 0);
        destination[0] = checked((byte)recordLength);
        BinaryPrimitives.WriteInt32LittleEndian(destination[2..], sector);
        BinaryPrimitives.WriteInt32BigEndian(destination[6..], sector);
        BinaryPrimitives.WriteInt32LittleEndian(destination[10..], length);
        BinaryPrimitives.WriteInt32BigEndian(destination[14..], length);
        destination[25] = 2;
        destination[28] = 1;
        destination[31] = 1;
        destination[32] = checked((byte)name.Length);
        name.CopyTo(destination[33..]);
    }

    private static async Task<string> HashFileAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await MD5.HashDataAsync(stream)).ToLowerInvariant();
    }

    private static void CreateHardLink(string path, string existingPath)
    {
        var created = OperatingSystem.IsWindows()
            ? CreateHardLinkWindows(path, existingPath, IntPtr.Zero)
            : CreateHardLinkUnix(existingPath, path) == 0;
        if (!created) throw new IOException($"Could not create hard-link test fixture: {Marshal.GetLastPInvokeError()}");
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int CreateHardLinkUnix(string existingPath, string newPath);

    [DllImport("Kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkWindows(string newPath, string existingPath, IntPtr securityAttributes);

    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }

    private static async Task<T> ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T exception) { return exception; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }

    private static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected {expected}, got {actual}");
    }

    private static void Contains(string actual, string expected, string context)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"{context}: expected '{actual}' to contain '{expected}'");
    }

    private sealed class TestPatchFaultException : Exception;
}
