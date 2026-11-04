using System.Buffers.Binary;
using System.Security.Cryptography;
using Forge.Host.Domain;
using Forge.Host.Games.GC;
using Forge.Host.Games.UYA;
using Forge.Host.ProtocolTests.Games.UYA;
using RatchetPs2.Core.Games;
using RatchetPs2.Games.GC.Level;
using RatchetPs2.Sdk;

namespace Forge.Host.ProtocolTests.Games.GC;

internal static class GcAssetImportTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-gc-import-{Guid.NewGuid():N}");
        try
        {
            var bytes = UyaAssetImportTests.CreateIso();
            foreach (var level in new[] { 1, 30 })
            {
                var entry = GcLevelCatalog.RetailLevelInfoTableOffset + GcLevelCatalog.GetById(level).TableIndex * GcLevelCatalog.LevelInfoSize;
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(entry), 0x500);
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(entry + 4), 2);
            }
            Check(LevelCatalogReader.FindAvailable(GameId.GC, new MemoryStream(bytes)).SequenceEqual([1, 30]),
                "GC level discovery maps the museum's table index to level 30");
            var request = new AssetImportRequest(GameId.GC, "synthetic.iso", root, new string('a', 32), "1.01", "gc-test");
            using var cancellation = new CancellationTokenSource();
            try
            {
                await GcAssetImportService.ImportValidatedAsync(new MemoryStream(bytes), request, value =>
                {
                    if (value.Completed == 1) cancellation.Cancel();
                    return ValueTask.CompletedTask;
                }, cancellation.Token);
                throw new Exception("Expected cancellation");
            }
            catch (OperationCanceledException) { }
            var result = await GcAssetImportService.ImportValidatedAsync(new MemoryStream(bytes), request);
            Check(result.Resumed && result.CompletedLevels == 2 && result.TotalLevels == 2
                && result.AssetAppearances == 12 && result.UniqueAssets == 6 && result.FailedAssets == 0,
                "GC imports resume and deduplicate across levels");
            var catalog = await AssetCatalogStore.OpenAsync(root);
            foreach (var entry in catalog.Query(new()))
            {
                Check(entry.Sources.Count == 2 && entry.Sources.All(source => source.Game == "GC" && source.Revision == "1.01"), "GC provenance");
                var blob = await File.ReadAllBytesAsync(catalog.ResolveBlobPath(entry.Id)!);
                Check(AssetId.Compute(entry.Kind, entry.CanonicalFormatVersion, blob) == entry.Id, "GC canonical hash");
                if (entry.Kind is AssetKind.Moby or AssetKind.Tie or AssetKind.Shrub)
                {
                    Check(CatalogModelCodec.Decode(blob).Game == GameId.GC, "GC canonical envelope");
                    Check(!UyaCanonicalAssetCodec.IsValid(blob), "GC models stay separate from native UYA assets");
                }
            }
            var missing = catalog.Query(new(Kind: AssetKind.Tie)).Single();
            File.Delete(catalog.ResolveBlobPath(missing.Id)!);
            Check(!(await GcAssetImportService.ImportValidatedAsync(new MemoryStream(bytes), request)).Resumed,
                "GC import restores missing checkpoint blobs");
            Check(!(await GcAssetImportService.ImportValidatedAsync(new MemoryStream(bytes), request with { Force = true })).Resumed,
                "GC forced reimport");
            Check(Directory.Exists(Path.Combine(root, "imports", "gc")) && !Directory.Exists(Path.Combine(root, "imports", "uya")),
                "GC checkpoints are isolated");
            DiscImageFixture.AddIdentity(bytes, "SCUS_972.68", revision: "1.01");
            var path = Path.Combine(root, "source.iso");
            await File.WriteAllBytesAsync(path, bytes);
            await using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();
            var identity = await IsoSourceService.ValidateAsync(GameId.GC, stream, expectedMd5: hash, expectedSize: bytes.Length);
            Check(identity.IsSupported && identity.Game == "GC" && identity.Revision == "1.01", "GC disc identity");
            Check(!(await IsoSourceService.ValidateAsync(GameId.UYA, stream, expectedMd5: hash, expectedSize: bytes.Length)).IsSupported,
                "GC disc is rejected as UYA");
            Check(!(await IsoSourceService.ValidateAsync(GameId.GC, stream, expectedMd5: new string('0', 32), expectedSize: bytes.Length)).IsSupported,
                "modified GC disc is rejected");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    public static async Task QualifyAsync(string iso, string catalogRoot)
    {
        var identity = await IsoSourceService.ValidateAsync(GameId.GC, iso);
        Check(identity.IsSupported, identity.Diagnostic);
        var result = await GcAssetImportService.ImportAsync(new(GameId.GC, iso, catalogRoot, identity.Fingerprint,
            identity.Revision, GcAssetImportService.ImporterVersionPrefix + "qualification"), value =>
        {
            Console.WriteLine($"GC import {value.Completed}/{value.Total}");
            return ValueTask.CompletedTask;
        });
        Console.WriteLine($"GC qualified: {result.CompletedLevels} levels, {result.AssetAppearances} appearances, {result.UniqueAssets} unique assets, {result.FailedAssets} failures.");
        var catalog = await AssetCatalogStore.OpenReadOnlyAsync(catalogRoot);
        foreach (var kind in new[] { AssetKind.Moby, AssetKind.Tie, AssetKind.Shrub })
        {
            var entries = catalog.Query(new(Kind: kind, Game: "GC", Limit: 1000));
            var entry = entries.First(value => !value.Tags.Contains("texture:placeholder"));
            var bytes = await File.ReadAllBytesAsync(catalog.ResolveBlobPath(entry.Id)!);
            var package = await CatalogModelPreviewService.BuildAsync(kind, bytes, default);
            Check(package.Entries.Any(file => file.Path == "model.gltf"), $"GC {kind} preview");
            var source = CatalogModelCodec.Decode(bytes);
            var frontendKind = Enum.Parse<RatchetPs2.Core.LevelAssets.FrontendAssetKind>(kind.ToString());
            var converted = LevelAssetModelConverter.Convert(GameId.GC, GameId.UYA, frontendKind, source.ModelBytes, 0x9000);
            var textures = source.Textures.Select(value => value with { PifBytes = LevelAssetModelConverter.ConvertTexture(
                GameId.GC, GameId.UYA, value.PifBytes, (RatchetPs2.Core.Textures.Palettes.TextureRole)value.Role) }).ToArray();
            Check((await CatalogModelPreviewService.BuildAsync(kind,
                UyaCanonicalAssetCodec.Encode(source.DefinitionBytes, converted, textures), default)).Entries.Any(file => file.Path == "model.gltf"),
                $"GC {kind} converted UYA preview");
        }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
