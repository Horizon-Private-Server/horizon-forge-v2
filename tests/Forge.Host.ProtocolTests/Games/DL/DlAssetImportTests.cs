using System.Buffers.Binary;
using System.Security.Cryptography;
using Forge.Host.Domain;
using Forge.Host.Games.DL;
using Forge.Host.Games.UYA;
using RatchetPs2.Core.Games;
using RatchetPs2.Games.DL.Level;

namespace Forge.Host.ProtocolTests.Games.DL;

internal static class DlAssetImportTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-dl-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var bytes = CreateIso();
            var request = new AssetImportRequest(GameId.DL, "synthetic.iso", root, new string('a', 32), "1.00", "test-dl-v1");
            using var cancellation = new CancellationTokenSource();
            try
            {
                await DlAssetImportService.ImportValidatedAsync(new MemoryStream(bytes), request, value =>
                {
                    if (value.Completed == 1) cancellation.Cancel();
                    return ValueTask.CompletedTask;
                }, cancellation.Token);
                throw new Exception("Expected cancellation");
            }
            catch (OperationCanceledException) { }
            var result = await DlAssetImportService.ImportValidatedAsync(new MemoryStream(bytes), request);
            Check(result.Resumed && result.CompletedLevels == 2 && result.FailedAssets == 0, "DL import resumes");
            Check(result.AssetAppearances == 10 && result.UniqueAssets == 5, "DL catalog deduplicates appearances");
            var catalog = await AssetCatalogStore.OpenAsync(root);
            foreach (var entry in catalog.Query(new()))
            {
                Check(entry.Sources.Count == 2 && entry.Sources.All(source => source.Game == "DL"), "DL provenance");
                var blob = await File.ReadAllBytesAsync(catalog.ResolveBlobPath(entry.Id)!);
                Check(AssetId.Compute(entry.Kind, entry.CanonicalFormatVersion, blob) == entry.Id, "catalog blob hash");
                if (entry.Kind != AssetKind.Texture)
                {
                    Check(CatalogModelCodec.Decode(blob).Game == GameId.DL, "DL model envelope");
                    Check(!UyaCanonicalAssetCodec.IsValid(blob), "DL models cannot enter UYA map-native paths");
                }
            }
            var missing = catalog.Query(new(Kind: AssetKind.Tie)).Single();
            File.Delete(catalog.ResolveBlobPath(missing.Id)!);
            var repaired = await DlAssetImportService.ImportValidatedAsync(new MemoryStream(bytes), request);
            Check(!repaired.Resumed && repaired.AssetAppearances == 10, "missing blobs restart completed import");
            var forced = await DlAssetImportService.ImportValidatedAsync(new MemoryStream(bytes), request with { Force = true });
            Check(!forced.Resumed && forced.UniqueAssets == 5, "forced reimport");
            Check(Directory.Exists(Path.Combine(root, "imports", "dl")) && !Directory.Exists(Path.Combine(root, "imports", "uya")), "game-specific checkpoint isolation");
            Check(!Directory.EnumerateFiles(root, "*.partial", SearchOption.AllDirectories).Any(), "atomic publication cleanup");
            await VerifyPartialCheckpointRecoveryAsync(Path.Combine(root, "partial-recovery"));
            await VerifyIdentityAsync(root, bytes);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task VerifyPartialCheckpointRecoveryAsync(string root)
    {
        var bytes = CreateIso(distinctTies: true);
        var request = new AssetImportRequest(GameId.DL, "synthetic.iso", root, new string('a', 32), "1.00", "test-dl-recovery");
        using var cancellation = new CancellationTokenSource();
        try
        {
            await DlAssetImportService.ImportValidatedAsync(new MemoryStream(bytes), request, value =>
            {
                if (value.Completed == 1) cancellation.Cancel();
                return ValueTask.CompletedTask;
            }, cancellation.Token);
            throw new Exception("Expected cancellation after the first level");
        }
        catch (OperationCanceledException) { }

        var catalog = await AssetCatalogStore.OpenAsync(root);
        var removed = catalog.Query(new(Kind: AssetKind.Tie)).Single();
        Check(removed.Sources.Single().Level == "level01", "the partial checkpoint contains only the first level");
        var retained = catalog.Query(new()).Where(entry => entry.Id != removed.Id).Select(entry => entry.Id).ToArray();
        var cleanup = catalog.PreviewGarbageCollection(retained);
        await catalog.CollectGarbageAsync(retained, cleanup.ConfirmationToken);
        Check(catalog.ResolveBlobPath(removed.Id) is null, "catalog cleanup removes the earlier level's asset");

        var result = await DlAssetImportService.ImportValidatedAsync(new MemoryStream(bytes), request);
        catalog = await AssetCatalogStore.OpenReadOnlyAsync(root);
        Check(catalog.ResolveBlobPath(removed.Id) is not null,
            "resuming a partial import restores earlier assets removed by catalog cleanup");
        Check(!result.Resumed && result.CompletedLevels == 2 && result.TotalLevels == 2,
            "a partial checkpoint with missing assets restarts extraction");
        Check(result.AssetAppearances == 10 && result.UniqueAssets == 6 && result.FailedAssets == 0,
            "restart resets import counts instead of retaining stale progress");
        Check(catalog.Query(new()).Count == result.UniqueAssets && catalog.Query(new(Kind: AssetKind.Tie)).Count == 2,
            "both distinct level assets are present after recovery");
        foreach (var entry in catalog.Query(new()))
        {
            var blob = await File.ReadAllBytesAsync(catalog.ResolveBlobPath(entry.Id)!);
            Check(AssetId.Compute(entry.Kind, entry.CanonicalFormatVersion, blob) == entry.Id,
                "recovered catalog blobs retain their canonical identities");
        }
        var cached = await DlAssetImportService.ImportValidatedAsync(new MemoryStream(), request);
        Check(cached.Resumed && cached.AssetAppearances == result.AssetAppearances && cached.UniqueAssets == result.UniqueAssets,
            "the recovered completed checkpoint can be reused");
    }

    public static async Task QualifyAsync(string source, string catalog)
    {
        var identity = await IsoSourceService.ValidateAsync(GameId.DL, source);
        Check(identity.IsSupported, identity.Diagnostic);
        var request = new AssetImportRequest(GameId.DL, source, catalog, identity.Fingerprint,
            identity.Revision, DlAssetImportService.ImporterVersionPrefix + "qualification");
        var result = await DlAssetImportService.ImportAsync(request, value =>
        {
            Console.WriteLine($"DL import {value.Completed}/{value.Total}");
            return ValueTask.CompletedTask;
        });
        Console.WriteLine($"DL qualified: {result.CompletedLevels} levels, {result.AssetAppearances} appearances, {result.UniqueAssets} unique assets, {result.FailedAssets} failures.");
        var store = await AssetCatalogStore.OpenAsync(catalog);
        foreach (var kind in new[] { AssetKind.Moby, AssetKind.Tie, AssetKind.Shrub })
        {
            var entry = store.Query(new(Kind: kind, Game: "DL")).First();
            var package = await CatalogModelPreviewService.BuildAsync(kind, await File.ReadAllBytesAsync(store.ResolveBlobPath(entry.Id)!), default);
            Check(package.Entries.Any(file => file.Path == "model.gltf"), $"{kind} preview package");
        }
    }

    private static async Task VerifyIdentityAsync(string root, byte[] bytes)
    {
        DiscImageFixture.AddIdentity(bytes, "SCUS_974.65");
        var path = Path.Combine(root, "source.iso");
        await File.WriteAllBytesAsync(path, bytes);
        await using var stream = File.OpenRead(path);
        var md5 = Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();
        var identity = await IsoSourceService.ValidateAsync(GameId.DL, stream, expectedMd5: md5, expectedSize: bytes.Length);
        Check(identity.IsSupported && identity.Game == "DL", "DL disc identity");
        var wrongGame = await IsoSourceService.ValidateAsync(GameId.UYA, stream, expectedMd5: md5, expectedSize: bytes.Length);
        Check(!wrongGame.IsSupported, "DL disc rejected for UYA");
        var wrongHash = await IsoSourceService.ValidateAsync(GameId.DL, stream, expectedMd5: new string('0', 32), expectedSize: bytes.Length);
        Check(!wrongHash.IsSupported, "modified DL disc rejected");
    }

    private static byte[] CreateIso(bool distinctTies = false)
    {
        const int sector = 1200;
        var iso = new byte[(sector + (distinctTies ? 8 : 4)) * DlLevelConstants.SectorSize];
        foreach (var level in new[] { 1, 2 })
        {
            var wad = CreateWad();
            var headerSector = sector + (distinctTies ? (level - 1) * 4 : 0);
            var entry = DlLevelConstants.RetailLevelInfoTableOffset + level * DlLevelConstants.LevelInfoSize;
            Write(iso, entry + 8, headerSector); Write(iso, entry + 12, 4);
            Write(wad, 4, headerSector);
            if (distinctTies) wad[2 * DlLevelConstants.SectorSize + 0x420] = (byte)level;
            wad.CopyTo(iso, headerSector * DlLevelConstants.SectorSize);
        }
        return iso;
    }
    private static byte[] CreateWad()
    {
        var wad = new byte[4 * DlLevelConstants.SectorSize];
        Write(wad, 0, DlLevelConstants.LevelWadHeaderSize);
        Write(wad, 0x18, 2); Write(wad, 0x1c, 1);
        Write(wad, 0x460, 3); Write(wad, 0x464, 1);
        var core = wad.AsSpan(2 * DlLevelConstants.SectorSize);
        Write(core, 0x10, 0x100); Write(core, 0x14, 0x200);
        Write(core, 0x18, 0x300); Write(core, 0x1c, 1);
        Write(core, 0x50, 0x400); Write(core, 0x54, 0x50);
        var header = core[0x100..];
        Write(header, 0x18, 1); Write(header, 0x1c, 0xc0);
        Write(header, 0x20, 1); Write(header, 0x24, 0xe0);
        Write(header, 0x28, 1); Write(header, 0x2c, 0x100);
        Definition(header, 0xc0, 0x10, 100); header[0xd0] = 0;
        Definition(header, 0xe0, 0x20, 200);
        Definition(header, 0x100, 0x30, 300);
        core.Slice(0x410, 0x10).Fill(0x11);
        core.Slice(0x420, 0x10).Fill(0x22);
        core.Slice(0x430, 0x20).Fill(0x33);
        var mission = wad.AsSpan(3 * DlLevelConstants.SectorSize);
        Write(mission, 0, 0x40); Write(mission, 8, 0x100); Write(mission, 12, 0x40);
        var classes = mission[0x100..];
        Write(classes, 0, 1); Write(classes, 0x10, 999); Write(classes, 0x14, 0x20);
        classes.Slice(0x20, 0x20).Fill(0x44);
        return wad;
    }

    private static void Definition(Span<byte> data, int offset, int modelOffset, int id)
    {
        Write(data, offset, modelOffset); Write(data, offset + 4, id);
        data.Slice(offset + 0x10, 0x10).Fill(255);
    }

    private static void Write(Span<byte> data, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(data[offset..], value);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
