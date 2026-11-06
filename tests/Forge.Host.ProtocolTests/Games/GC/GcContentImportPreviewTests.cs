using System.Buffers.Binary;
using Forge.Host.Bridge;
using Forge.Host.Domain;
using Forge.Host.Games.GC;
using Forge.Host.Games.UYA;
using Forge.Host.ProtocolTests.Games.UYA;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.LevelAssets;
using RatchetPs2.Core.Tfrags;
using RatchetPs2.Games.UYA.Gameplay;
using RatchetPs2.Sdk;

namespace Forge.Host.ProtocolTests.Games.GC;

internal static class GcContentImportPreviewTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-gc-content-preview-{Guid.NewGuid():N}");
        try
        {
            var wad = CreateWad();
            var inventory = LevelContentInventoryReader.Read(GameId.GC, wad);
            var catalog = await AssetCatalogStore.OpenAsync(Path.Combine(root, "catalog"));
            var fingerprint = new string('b', 32);
            var entry = await catalog.PutAsync(AssetKind.Tie, 1, new byte[] { 1, 2, 3 }, new("preview-test",
                new("GC", "NTSC-U", "1.01", "level01", "level_wad/assets/asset_wad.bin", 0, fingerprint),
                ["tie:0x00C8"]));
            await catalog.PutAsync(AssetKind.Tie, 1, new byte[] { 1, 2, 3 }, new("preview-test",
                new("UYA", "NTSC-U", "1.00", "level01", "level_wad/assets/asset_wad.bin", 0, fingerprint), ["tie:200"]));
            var conversion = LevelContentConverter.Convert(GameId.GC, GameId.UYA, wad);
            var native = UyaBaseLevelService.ReadConverted(wad, conversion, catalog, 1,
                new MobyDexCatalog(UyaMobyDexDataset.Create()), "GC", default);
            VerifyOcclusionInventory(native);
            var target = new ProjectEntity(EntityId.New(), "Locked target", "ties", ProjectTransform.Identity,
                new(entry.Id, AssetKind.Tie), new("UYA", 40, "gameplay/core/tie_instances", 0),
                State: new(Locked: true), Source: new(200, new byte[0x60], false));
            var workspace = await ForgeProjectWorkspace.CreateAsync(Path.Combine(root, "project"), "GC preview",
                new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
                new("UYA", "NTSC-U", "1.00", 40, new string('a', 32)), [target]);
            var before = workspace.CurrentFingerprint;
            var identity = new IsoIdentity(true, "GC", "NTSC-U", "1.01", "SCUS-97268", 100, fingerprint, "verified fixture");
            var source = new ContentImportSource(identity, new string('c', 64), inventory);
            var request = new UyaConvertedContentImportPlanRequest(GameId.GC, Path.Combine(root, "project"), catalog.RootPath, "source.iso",
                fingerprint, 1, [new("ties", ContentImportMode.Replace)]);
            var plan = UyaConvertedContentImportService.CreatePlan(request, workspace, source, native);
            var ties = plan.Families.Single(value => value.Family == "ties");
            Check(plan.Game == "GC" && plan.Revision == "1.01" && plan.SourceFingerprint == fingerprint,
                "source identity is GC, independent of target UYA identity");
            Check(plan.TargetFingerprint == before && plan.TargetLevel == 40, "active UYA target identity");
            Check(ties.Selected && ties.Add.Count == 2 && ties.Protect.SequenceEqual([$"entity:{target.EntityId}"])
                && ties.Remove.Count == 0 && ties.Reuse.Count == 1, "replace preview protects locked targets and reports reuse");
            Check(plan.BatchId == UyaConvertedContentImportService.CreatePlan(request, workspace, source, native).BatchId,
                "deterministic preview");
            Check(workspace.CurrentFingerprint == before && workspace.Content.ImportBatches.Count == 0,
                "preview never changes the project");
            var missing = UyaConvertedContentImportService.CreatePlan(request, workspace, source, native,
                new Dictionary<string, string> { ["ties"] = "Missing donor model" });
            Check(missing.Families.Single(value => value.Family == "ties").SourceState == ContentImportSourceState.Malformed
                && missing.Blockers.Count > 0, "missing catalog models block the selected family");
            Throws<InvalidDataException>(() => ContentImportSourceCache.ValidateIdentity(identity, GameId.GC, "NTSC-U", "1.01", new string('a', 32)));
            Throws<InvalidDataException>(() => ContentImportSourceCache.ValidateIdentity(identity with { Game = "UYA" }, GameId.GC, "NTSC-U", "1.01", fingerprint));
            var payload = BridgePayloadCodec.EncodeContentImportPlanRequest(new(Path.Combine(root, "project"), catalog.RootPath,
                "source.iso", fingerprint, 1, [], "GC"));
            Check(BridgePayloadCodec.DecodeContentImportPlanRequest(payload).SourceGame == "GC", "GC source survives the bridge");
            Check(BridgePayloadCodec.DecodeContentImportPlanRequest(BridgePayloadCodec.EncodeContentImportPlanRequest(new(
                Path.Combine(root, "project"), catalog.RootPath, "source.iso", fingerprint, 1, []))).SourceGame == "UYA",
                "existing UYA requests retain their wire format");
            Console.WriteLine("GC donor inventory and read-only content preview checks passed.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    internal static byte[] CreateWad() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "gc-content.wad"));

    private static void VerifyOcclusionInventory(UyaBaseLevelData original)
    {
        var terrain = UyaTfragProjectTests.SyntheticTerrain(1);
        BinaryPrimitives.WriteInt16LittleEndian(terrain.AsSpan(0x40 + 0x3a), 1);
        var payload = new UyaBaseLayerPayload(BakeLayerId.Tfrags, "primary.bin", AssetKind.Tfrag,
            "level_wad/assets/asset_wad.bin", 0, terrain);
        var mappings = new byte[0x10 + 5 * 8];
        Write(mappings, 0, 2); Write(mappings, 4, 2); Write(mappings, 8, 1);
        Write(mappings, 0x10, -1); Write(mappings, 0x18, 9);
        Write(mappings, 0x34, 11);
        var mobys = Enumerable.Range(10, 2).Select(uid =>
        {
            var raw = new byte[0x88]; Write(raw, 0, 0x88); Write(raw, 0x10, uid);
            return new ProjectEntity(EntityId.New(), "GC Moby", "mobys", ProjectTransform.Identity,
                null, new("UYA", 1, "gameplay/core/moby_instances", uid - 10), Source: new(0, raw, true));
        });
        var source = original with
        {
            Entities = original.Entities.Concat(mobys).ToArray(),
            BaseLayers = [payload], Tfrags = UyaTfragProjectService.Create([payload], 1),
            Occlusion = new(mappings, new byte[8], [1]),
        };
        Check(State(source) == ContentImportSourceState.Present,
            "sparse Moby mappings and shared streamed terrain mappings are valid: "
                + UyaContentImportPlanningService.Inventory(source).Single(value => value.Family == "occlusion").Diagnostic);
        var invalid = mappings.ToArray(); Write(invalid, 0x34, 999);
        Check(State(source with { Occlusion = source.Occlusion with { GameplayMappings = invalid } }) == ContentImportSourceState.Present,
            "unused authored Moby mappings do not hide donor occlusion");
        var missing = source.Tfrags.Entities[0];
        Check(State(source with { Tfrags = source.Tfrags with
        {
            Entities = [missing with { Tfrag = missing.Tfrag! with { OcclusionIndex = 2 } }],
        } }) == ContentImportSourceState.Malformed, "missing terrain mapping slots remain blocked");

        static ContentImportSourceState State(UyaBaseLevelData source) =>
            UyaContentImportPlanningService.Inventory(source).Single(value => value.Family == "occlusion").State;
    }

    private static void Write(Span<byte> bytes, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes[offset..], value);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception($"GC content preview: {message}"); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}");
    }
}
