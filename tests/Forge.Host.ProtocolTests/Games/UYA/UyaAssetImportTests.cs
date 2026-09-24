using Forge.Host.Games.UYA;
using System.Buffers.Binary;
using Forge.Host.Domain;
using RatchetPs2.Games.UYA.Level;

namespace Forge.Host.ProtocolTests.Games.UYA;

internal static class UyaAssetImportTests
{
    public static async Task RunAsync()
    {
        var definitionA = new byte[0x20];
        var definitionB = new byte[0x20];
        WriteInt32(definitionA, 0, 0x100);
        WriteInt32(definitionB, 0, 0x900);
        definitionA.AsSpan(0x10).Fill(1);
        definitionB.AsSpan(0x10).Fill(2);
        Equal(true, UyaCanonicalAssetCodec.Encode(definitionA, [1], [])
            .SequenceEqual(UyaCanonicalAssetCodec.Encode(definitionB, [1], [])),
            "volatile native references do not split canonical assets");

        var root = Path.Combine(Path.GetTempPath(), $"forge-uya-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var request = new UyaAssetImportRequest(
                "synthetic.iso", root, new string('a', 32), "1.00", "test-importer-v0");
            var isoBytes = CreateIso();
            var cancellation = new CancellationTokenSource();
            await ThrowsCancelledAsync(() => UyaAssetImportService.ImportValidatedAsync(
                new MemoryStream(isoBytes, writable: false),
                request,
                progress =>
                {
                    if (progress.Completed == 1) cancellation.Cancel();
                    return ValueTask.CompletedTask;
                },
                cancellation.Token));

            var result = await UyaAssetImportService.ImportValidatedAsync(
                new MemoryStream(isoBytes, writable: false), request);
            Equal(true, result.Resumed, "resumed import");
            Equal(2, result.CompletedLevels, "completed levels");
            Equal(2, result.TotalLevels, "total levels");
            Equal(8, result.AssetAppearances, "asset appearances");
            Equal(4, result.UniqueAssets, "deduplicated assets");
            Equal(0, result.FailedAssets, "failed assets");

            var catalog = await AssetCatalogStore.OpenAsync(root);
            foreach (var kind in new[] { AssetKind.Tie, AssetKind.Shrub })
            {
                var entry = catalog.Query(new(Kind: kind)).Single();
                Equal(2, entry.Sources.Count, $"{kind} source appearances");
                Equal(true, entry.Tags.Contains("level:01"), $"{kind} level 1 tag");
                Equal(true, entry.Tags.Contains("level:02"), $"{kind} level 2 tag");
            }
            var mobys = catalog.Query(new(Kind: AssetKind.Moby));
            Equal(2, mobys.Count, "moby model count");
            Equal(1, mobys.Count(entry => entry.Tags.Contains("texture:placeholder")), "placeholder moby count");
            foreach (var entry in mobys) Equal(2, entry.Sources.Count, "moby source appearances");

            var repairTarget = mobys[0];
            File.Delete(catalog.ResolveBlobPath(repairTarget.Id)!);
            var repair = await UyaAssetImportService.RepairLevelsValidatedAsync(
                new MemoryStream(isoBytes, writable: false), request, [1]);
            Equal(1, repair.CompletedLevels, "level-scoped repair count");
            catalog = await AssetCatalogStore.OpenAsync(root);
            Equal(true, catalog.ResolveBlobPath(repairTarget.Id) is not null, "level-scoped repair restores missing blob");

            var cached = await UyaAssetImportService.ImportValidatedAsync(new MemoryStream(), request);
            Equal(true, cached.Resumed, "completed import reuse");
            Equal(result.UniqueAssets, cached.UniqueAssets, "completed import asset count");

            catalog = await AssetCatalogStore.OpenAsync(root);
            var missingAfterCheckpoint = catalog.Query(new(Kind: AssetKind.Tie)).Single();
            File.Delete(catalog.ResolveBlobPath(missingAfterCheckpoint.Id)!);
            var repairedCheckpoint = await UyaAssetImportService.ImportValidatedAsync(
                new MemoryStream(isoBytes, writable: false), request);
            Equal(false, repairedCheckpoint.Resumed, "incomplete completed-checkpoint rescan");
            catalog = await AssetCatalogStore.OpenAsync(root);
            Equal(true, catalog.ResolveBlobPath(missingAfterCheckpoint.Id) is not null,
                "completed-checkpoint rescan restores missing blob");

            var forcedCancellation = new CancellationTokenSource();
            await ThrowsCancelledAsync(() => UyaAssetImportService.ImportValidatedAsync(
                new MemoryStream(isoBytes, writable: false),
                request with { Force = true },
                progress =>
                {
                    if (progress.Completed == 1) forcedCancellation.Cancel();
                    return ValueTask.CompletedTask;
                },
                forcedCancellation.Token));
            var forced = await UyaAssetImportService.ImportValidatedAsync(
                new MemoryStream(isoBytes, writable: false), request with { Force = true });
            Equal(true, forced.Resumed, "forced import resumes its interrupted rescan");
            Equal(result.AssetAppearances, forced.AssetAppearances, "forced import appearance count");
            Equal(result.UniqueAssets, forced.UniqueAssets, "forced import asset count");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] CreateIso()
    {
        const int headerSector = 0x500;
        var bytes = new byte[(headerSector + 2) * UyaLevelConstants.SectorSize];
        WriteLevelInfo(bytes, 1, headerSector);
        WriteLevelInfo(bytes, 2, headerSector);

        var wad = bytes.AsSpan(headerSector * UyaLevelConstants.SectorSize);
        WriteInt32(wad, 0x00, UyaLevelConstants.LevelWadHeaderSize);
        WriteInt32(wad, 0x04, headerSector);
        WriteInt32(wad, 0x08, 1);
        WriteInt32(wad, 0x10, 1);
        WriteInt32(wad, 0x14, 1);

        var levelData = wad[UyaLevelConstants.SectorSize..];
        WriteByteBlock(levelData, 0x08, 0x100, 0x160);
        WriteByteBlock(levelData, 0x10, 0x240, 1);
        WriteByteBlock(levelData, 0x48, 0x300, 0x50);

        var assetHeader = levelData[0x100..];
        WriteInt32(assetHeader, 0x18, 2);
        WriteInt32(assetHeader, 0x1c, 0xc0);
        WriteInt32(assetHeader, 0x20, 1);
        WriteInt32(assetHeader, 0x24, 0x100);
        WriteInt32(assetHeader, 0x28, 1);
        WriteInt32(assetHeader, 0x2c, 0x120);
        WriteInt32(assetHeader, 0x38, 1);
        WriteInt32(assetHeader, 0x3c, 0x150);
        WriteModelDefinition(assetHeader, 0xc0, 0x10, 100);
        WriteModelDefinition(assetHeader, 0xe0, 0x20, 101);
        assetHeader[0xf0] = 0;
        WriteModelDefinition(assetHeader, 0x100, 0x30, 200);
        WriteModelDefinition(assetHeader, 0x120, 0x40, 300);
        WriteInt32(assetHeader, 0x150, 0x1234);
        BinaryPrimitives.WriteInt16LittleEndian(assetHeader[0x154..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(assetHeader[0x156..], 1);

        var assets = levelData[0x300..0x350];
        assets[0x10..0x20].Fill(0x11);
        assets[0x20..0x30].Fill(0x22);
        assets[0x30..0x40].Fill(0x33);
        assets[0x40..0x50].Fill(0x44);
        return bytes;
    }

    private static void WriteLevelInfo(byte[] iso, int level, int headerSector)
    {
        var offset = UyaLevelConstants.RetailLevelInfoTableOffset + level * UyaLevelConstants.LevelInfoSize;
        WriteInt32(iso, offset + 0x08, headerSector);
        WriteInt32(iso, offset + 0x0c, 2);
    }

    private static void WriteByteBlock(Span<byte> bytes, int offset, int blockOffset, int length)
    {
        WriteInt32(bytes, offset, blockOffset);
        WriteInt32(bytes, offset + 4, length);
    }

    private static void WriteModelDefinition(Span<byte> bytes, int offset, int modelOffset, int modelId)
    {
        WriteInt32(bytes, offset, modelOffset);
        WriteInt32(bytes, offset + 4, modelId);
        bytes.Slice(offset + 0x10, 0x10).Fill(byte.MaxValue);
    }

    private static void WriteInt32(Span<byte> bytes, int offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(bytes[offset..], value);

    private static async Task ThrowsCancelledAsync(Func<Task> action)
    {
        try
        {
            await action();
            throw new InvalidOperationException("Expected asset import cancellation.");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected {expected}, got {actual}");
    }
}
