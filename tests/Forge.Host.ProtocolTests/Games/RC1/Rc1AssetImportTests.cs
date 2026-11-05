using System.Buffers.Binary;
using System.Security.Cryptography;
using Forge.Host.Domain;
using Forge.Host.Games.RC1;
using Forge.Host.Games.UYA;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.Moby;
using RatchetPs2.Games.RC1.Level;
using RatchetPs2.Sdk;

namespace Forge.Host.ProtocolTests.Games.RC1;

internal static class Rc1AssetImportTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-rc1-import-{Guid.NewGuid():N}");
        try
        {
            var bytes = CreateIso();
            Check(LevelCatalogReader.FindAvailable(GameId.RC1, new MemoryStream(bytes)).SequenceEqual([0, 18]),
                "RC1 discovers actual level IDs independently of table indices");
            var request = new AssetImportRequest(GameId.RC1, "synthetic.iso", root, new string('a', 32), "1.00", "rc1-test");
            using var cancellation = new CancellationTokenSource();
            try
            {
                await Rc1AssetImportService.ImportValidatedAsync(new MemoryStream(bytes), request, value =>
                {
                    if (value.Completed == 1) cancellation.Cancel();
                    return ValueTask.CompletedTask;
                }, cancellation.Token);
                throw new Exception("Expected cancellation");
            }
            catch (OperationCanceledException) { }
            var result = await Rc1AssetImportService.ImportValidatedAsync(new MemoryStream(bytes), request);
            Check(result.Resumed && result.CompletedLevels == 2 && result.AssetAppearances == 8 && result.UniqueAssets == 4
                && result.FailedAssets == 0, "RC1 import resumes and deduplicates models and textures");
            var catalog = await AssetCatalogStore.OpenAsync(root);
            foreach (var entry in catalog.Query(new()))
            {
                Check(entry.Sources.Count == 2 && entry.Sources.All(source => source.Game == "RC1" && source.Revision == "1.00"), "RC1 provenance");
                var blob = await File.ReadAllBytesAsync(catalog.ResolveBlobPath(entry.Id)!);
                Check(AssetId.Compute(entry.Kind, entry.CanonicalFormatVersion, blob) == entry.Id, "RC1 canonical hash");
                if (entry.Kind is AssetKind.Moby or AssetKind.Tie or AssetKind.Shrub)
                {
                    Check(CatalogModelCodec.Decode(blob).Game == GameId.RC1, "RC1 canonical envelope");
                    if (entry.Kind != AssetKind.Shrub)
                        Check((await CatalogModelPreviewService.BuildAsync(entry.Kind, blob, default)).Entries.Any(file => file.Path == "model.gltf"), "RC1 catalog preview");
                }
            }
            File.Delete(catalog.ResolveBlobPath(catalog.Query(new(Kind: AssetKind.Tie)).Single().Id)!);
            Check(!(await Rc1AssetImportService.ImportValidatedAsync(new MemoryStream(bytes), request)).Resumed, "RC1 restores missing blobs");
            Check(!(await Rc1AssetImportService.ImportValidatedAsync(new MemoryStream(bytes), request with { Force = true })).Resumed, "RC1 forced reimport");
            Check(Directory.Exists(Path.Combine(root, "imports", "rc1")) && !Directory.Exists(Path.Combine(root, "imports", "gc")), "isolated RC1 checkpoints");
            DiscImageFixture.AddIdentity(bytes, "SCUS_971.99", revision: "1.00");
            var path = Path.Combine(root, "source.iso"); await File.WriteAllBytesAsync(path, bytes);
            await using var sourceStream = File.OpenRead(path);
            var identity = await IsoSourceService.ValidateAsync(GameId.RC1, sourceStream,
                expectedMd5: Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant(), expectedSize: bytes.Length);
            Check(identity.IsSupported && identity.Game == "RC1", "RC1 ISO identity");
            Check(!(await IsoSourceService.ValidateAsync(GameId.GC, sourceStream,
                expectedMd5: identity.Fingerprint, expectedSize: bytes.Length)).IsSupported, "RC1 is rejected as GC");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    public static async Task QualifyAsync(string iso, string catalogRoot)
    {
        var identity = await IsoSourceService.ValidateAsync(GameId.RC1, iso);
        Check(identity.IsSupported, identity.Diagnostic);
        var result = await Rc1AssetImportService.ImportAsync(new(GameId.RC1, iso, catalogRoot, identity.Fingerprint,
            identity.Revision, Rc1AssetImportService.ImporterVersionPrefix + "qualification"), value =>
        {
            Console.WriteLine($"RC1 import {value.Completed}/{value.Total}");
            return ValueTask.CompletedTask;
        });
        Console.WriteLine($"RC1 qualified: {result.CompletedLevels} levels, {result.AssetAppearances} appearances, {result.UniqueAssets} unique assets, {result.FailedAssets} failures.");
        var catalog = await AssetCatalogStore.OpenReadOnlyAsync(catalogRoot);
        foreach (var kind in new[] { AssetKind.Moby, AssetKind.Tie, AssetKind.Shrub })
        {
            var entries = catalog.Query(new(Kind: kind, Game: "RC1", Limit: 1000));
            var entry = entries.First(value => !value.Tags.Contains("texture:placeholder"));
            var bytes = await File.ReadAllBytesAsync(catalog.ResolveBlobPath(entry.Id)!);
            var package = await CatalogModelPreviewService.BuildAsync(kind, bytes, default);
            Check(package.Entries.Any(file => file.Path == "model.gltf"), $"RC1 {kind} preview");
            var source = CatalogModelCodec.Decode(bytes);
            var frontendKind = Enum.Parse<RatchetPs2.Core.LevelAssets.FrontendAssetKind>(kind.ToString());
            var converted = LevelAssetModelConverter.Convert(GameId.RC1, GameId.UYA, frontendKind, source.ModelBytes, 0x9000, textures: source.Textures);
            var textures = source.Textures.Select(value => value with { PifBytes = LevelAssetModelConverter.ConvertTexture(
                GameId.RC1, GameId.UYA, value.PifBytes, (RatchetPs2.Core.Textures.Palettes.TextureRole)value.Role) }).ToArray();
            Check((await CatalogModelPreviewService.BuildAsync(kind,
                UyaCanonicalAssetCodec.Encode(source.DefinitionBytes, converted, textures), default)).Entries.Any(file => file.Path == "model.gltf"),
                $"RC1 {kind} converted UYA preview");
        }
    }

    internal static byte[] CreateIso(bool transparent = false)
    {
        // Generated SDK fixture; no retail model or disc bytes.
        var tie = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "rc1-tie.bin"));
        var moby = MobyModelPacker.Build(new() { Scale = 1 });
        var tieOffset = Align16(16 + moby.Length);
        var shrubOffset = Align16(tieOffset + tie.Length);
        var textureOffset = shrubOffset + 0xc0;
        var assets = new byte[textureOffset + 1024];
        moby.CopyTo(assets, 16); tie.CopyTo(assets, tieOffset);
        var data = new byte[0x700 + assets.Length];
        WriteRange(data, 0x10, 0x100, 0x180); WriteRange(data, 0x18, 0x280, 0x400);
        WriteRange(data, 0x50, 0x700, assets.Length); assets.CopyTo(data, 0x700);
        for (var index = 0; index < 256; index++) data[0x280 + index * 4 + 3] = 128;
        if (transparent) data[0x283] = 0;
        var header = data.AsSpan(0x100, 0x180);
        I32(header, 0x18, 1); I32(header, 0x1c, 0xc0); I32(header, 0x20, 1); I32(header, 0x24, 0xe0);
        I32(header, 0x28, 1); I32(header, 0x2c, 0x100); I32(header, 0x40, 1); I32(header, 0x44, 0x140);
        I32(header, 0x60, textureOffset);
        // RC1 uses the later extra-mipmap slot for an unrelated pointer.
        I32(header, 0x84, 0x7c50);
        Definition(header, 0xc0, 16); Definition(header, 0xe0, tieOffset); Definition(header, 0x100, shrubOffset);
        header[0xf0] = 0;
        BinaryPrimitives.WriteInt16LittleEndian(header[0x144..], 32); BinaryPrimitives.WriteInt16LittleEndian(header[0x146..], 32);
        var sectors = (data.Length + 2047) / 2048;
        const int dataSector = 1610;
        var iso = new byte[(dataSector + sectors) * 2048];
        var toc = iso.AsSpan(Rc1LevelConstants.TableOfContentsSector * 2048);
        I32(toc, 0, 1); I32(toc, 4, Rc1LevelConstants.TableOfContentsSize);
        for (var index = 0; index < 2; index++)
        {
            var sector = 1600 + index * 5;
            WriteRange(toc, Rc1LevelConstants.LevelTableOffset + index * 8, sector, 5);
            var level = iso.AsSpan(sector * 2048);
            I32(level, 0, index == 0 ? 0 : 18); I32(level, 4, Rc1LevelConstants.AmalgamatedHeaderSize);
            WriteRange(level, 8, dataSector, sectors);
        }
        data.CopyTo(iso, dataSector * 2048);
        return iso;
    }

    private static void Definition(Span<byte> header, int offset, int modelOffset)
    {
        I32(header, offset, modelOffset); I32(header, offset + 4, 0x1234);
        header.Slice(offset + 16, 16).Fill(255);
    }
    private static int Align16(int value) => (value + 15) & ~15;
    private static void I32(Span<byte> bytes, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes[offset..], value);
    private static void WriteRange(Span<byte> bytes, int offset, int start, int length) { I32(bytes, offset, start); I32(bytes, offset + 4, length); }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
