using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using Forge.Host.Domain;
using RatchetPs2.Games.UYA.Level;

internal static class UyaIsoSetupTests
{
    public static async Task RunAsync()
    {
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

    private static byte[] CreateSyntheticIso(string serial, string videoMode)
    {
        const int rootSector = 20;
        const int systemSector = 21;
        const int levelWadSector = 1200;
        var bytes = new byte[1300 * UyaLevelConstants.SectorSize];

        var descriptor = bytes.AsSpan(16 * UyaLevelConstants.SectorSize, UyaLevelConstants.SectorSize);
        descriptor[0] = 1;
        "CD001"u8.CopyTo(descriptor[1..]);
        descriptor[6] = 1;
        WriteDirectoryRecord(descriptor[156..], rootSector, UyaLevelConstants.SectorSize, [0]);

        var root = bytes.AsSpan(rootSector * UyaLevelConstants.SectorSize, UyaLevelConstants.SectorSize);
        WriteDirectoryRecord(root, systemSector, 128, Encoding.ASCII.GetBytes("SYSTEM.CNF;1"));
        var configuration = Encoding.ASCII.GetBytes($"BOOT2 = cdrom0:\\{serial};1\r\nVER = 1.00\r\nVMODE = {videoMode}\r\n");
        configuration.CopyTo(bytes.AsSpan(systemSector * UyaLevelConstants.SectorSize));

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

    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
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
}
