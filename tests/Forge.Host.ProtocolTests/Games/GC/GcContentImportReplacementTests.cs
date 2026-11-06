using System.Buffers.Binary;
using System.Security.Cryptography;
using Forge.Host.Domain;
using Forge.Host.Games.GC;
using Forge.Host.Games.UYA;
using Forge.Host.ProtocolTests.Games.UYA;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.Gameplay;
using RatchetPs2.Core.LevelAssets;
using RatchetPs2.Core.Moby;
using RatchetPs2.Core.Textures.Pif;
using RatchetPs2.Games.GC.Gameplay;
using RatchetPs2.Games.UYA.Gameplay;
using RatchetPs2.Sdk;

namespace Forge.Host.ProtocolTests.Games.GC;

internal static class GcContentImportReplacementTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-gc-replace-{Guid.NewGuid():N}");
        try
        {
            var catalog = await AssetCatalogStore.OpenAsync(Path.Combine(root, "catalog"));
            var fingerprint = new string('b', 32);
            var palette = new byte[0x400];
            palette[0] = palette[1] = palette[2] = 255; palette[3] = 128;
            var texture = new FrontendAssetTexture(0, PifWriter.Write(PifWriter.CreateIndexed8(32, 32, palette, new byte[1024])));
            foreach (var (kind, classId) in new[] { (AssetKind.Tie, 200), (AssetKind.Moby, 100), (AssetKind.Moby, 101) })
            {
                var model = kind == AssetKind.Tie
                    ? await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "gc-folded-tie.bin"))
                    : MobyModelPacker.Build(new() { Scale = 1 });
                if (kind == AssetKind.Moby) model.AsSpan(0x0a, 2).Fill(0xff);
                var definition = new byte[0x20];
                Write(definition, 4, classId);
                var bytes = CatalogModelCodec.Encode(GameId.GC, definition, model,
                    kind == AssetKind.Tie ? [texture] : []);
                await catalog.PutAsync(kind, 1, bytes, new("gc-replace-test",
                    new("GC", "NTSC-U", "1.01", "level01", "level_wad/assets/asset_wad.bin", kind == AssetKind.Moby ? classId - 100 : 0, fingerprint),
                    [$"{kind.ToString().ToLowerInvariant()}:0x{classId:X4}"]));
            }
            // A retail level's textures and sounds can push models beyond the unpaged query limit.
            var lastModel = catalog.Query(new(Limit: AssetCatalogStore.MaxQueryLimit)).MaxBy(entry => entry.Id.ToString(), StringComparer.Ordinal)!.Id;
            var padding = new List<AssetCatalogPut>();
            for (var index = 0; padding.Count <= AssetCatalogStore.MaxQueryLimit; index++)
            {
                var bytes = BitConverter.GetBytes(index);
                if (string.CompareOrdinal(AssetId.Compute(AssetKind.Texture, 1, bytes).ToString(), lastModel.ToString()) >= 0) continue;
                padding.Add(new(AssetKind.Texture, 1, bytes, new("gc-replace-test",
                    new("GC", "NTSC-U", "1.01", "level01", "textures", index, fingerprint))));
            }
            padding.Add(new(AssetKind.Moby, 1, new byte[] { 1, 2, 3 }, new("gc-replace-test",
                new("GC", "NTSC-U", "1.01", "level01", "level_wad/assets/asset_wad.bin", 999, fingerprint),
                ["moby:0xFFFF"], ["texture:placeholder"])));
            await catalog.PutManyAsync(padding);
            Check(!catalog.Query(new(Game: "GC", Level: "level01", Limit: AssetCatalogStore.MaxQueryLimit))
                .Any(entry => entry.Id == lastModel), "fixture exceeds the old mixed-asset query limit");
            var path = Path.Combine(root, "project");
            var targetCollisionBytes = UyaRenderPackageTests.BuildCollisionFixture();
            var donorCollisionBytes = CollisionWork.Compose(targetCollisionBytes, GameId.UYA,
                [new(CollisionPieceKind.Solid, 0, 4, 0, 0)], []).Bytes;
            var targetSource = new OpaqueContentSource("UYA", "NTSC-U", "1.00", 40, new string('a', 32));
            var targetPayload = new UyaBaseLayerPayload(BakeLayerId.Collision, "collision.bin", AssetKind.Collision,
                "level_wad/assets/asset_wad.bin", 0, targetCollisionBytes);
            var targetEntry = (await catalog.PutManyAsync(
                UyaBaseLayerService.CreateCatalogPuts([targetPayload], targetSource, "gc-target-fixture"))).Single();
            await UyaBaseLayerStore.WriteAsync(path, targetSource, [targetPayload], [targetEntry]);
            var targetCollisions = UyaCollisionContentImportTests.Entities(
                CollisionConverter.Inspect(targetCollisionBytes, GameId.UYA), targetEntry.Id, 40);
            var wad = CreateWad();
            Write(wad, 0x914, 0x180);
            Write(wad, 0x84c, 0x180 + donorCollisionBytes.Length);
            donorCollisionBytes.CopyTo(wad, 0xc80);
            var raw = new ContentImportSource(new(true, "GC", "NTSC-U", "1.01", "SCUS-97268", wad.Length, fingerprint, "fixture"),
                Convert.ToHexString(SHA256.HashData(wad)).ToLowerInvariant(), LevelContentInventoryReader.Read(GameId.GC, wad), wad);
            var request = new UyaConvertedContentImportPlanRequest(GameId.GC, path, catalog.RootPath, "fixture.iso", fingerprint, 1,
                [new("ties", ContentImportMode.Replace), new("mobys", ContentImportMode.Replace),
                    new("cuboids", ContentImportMode.Replace), new("collision", ContentImportMode.Replace)]);
            var dex = new MobyDexCatalog(UyaMobyDexDataset.Create());
            var prepared = await UyaConvertedContentImportPreparation.CreateAsync(request, raw, dex, null, default);
            Check(prepared.Diagnostics.Count == 0, "fixture models convert: " + string.Join("; ", prepared.Diagnostics.Select(pair => $"{pair.Key}: {pair.Value}")));
            var donorTie = prepared.Source.Entities.First(entity => entity.Layer == "ties");
            var locked = donorTie with { EntityId = EntityId.New(), Name = "Locked target", State = new(Locked: true),
                Provenance = donorTie.Provenance! with { Game = "UYA", Level = 40, SourceIndex = 0 } };
            var oldTie = locked with { EntityId = EntityId.New(), Name = "Replace target", State = null,
                Provenance = locked.Provenance! with { SourceIndex = 1 } };
            var oldMoby = prepared.Source.Entities.First(entity => entity.Layer == "mobys") with
            {
                EntityId = EntityId.New(), Name = "Old target Moby",
                Provenance = new("UYA", 40, "gameplay/core/moby_instances", 0),
            };
            var oldRecord = oldMoby.Source!.RawRecord.ToArray();
            Write(oldRecord, 0x68, -1);
            oldMoby = oldMoby with { Source = oldMoby.Source with { RawRecord = oldRecord } };
            var workspace = await ForgeProjectWorkspace.CreateAsync(path, "GC replacement", new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
                new("UYA", "NTSC-U", "1.00", 40, new string('a', 32)), [locked, oldTie, oldMoby, .. targetCollisions],
                null, null, null, mobyPVars: new(ProjectMobyPVarSchema.CurrentVersion, 2, []));
            await UyaStaticLayerStore.WriteSourceAsync(path, new("UYA", "NTSC-U", "1.00", 40, new string('a', 32)),
            [new(BakeLayerId.Ties, 0x60, [0, 0, 0], []), new(BakeLayerId.Shrubs, 0x70, [0, 0, 0], []), new(BakeLayerId.Mobys, 0x88, [0, 0, 0], [])]);
            await UyaGameplayLayerStore.WriteSourceAsync(path, new("UYA", "NTSC-U", "1.00", 40, new string('a', 32)), prepared.Source.Gameplay);
            await using var runtime = new EditorRuntime();
            await runtime.OpenAsync(path, TimeSpan.FromDays(1));
            workspace = await runtime.ReadWorkspaceAsync(path, (active, _) => Task.FromResult(active.CreateProjection()));
            var before = workspace.CurrentFingerprint;
            var plan = UyaConvertedContentImportService.CreatePlan(request, workspace, raw, prepared.Source, prepared.Diagnostics);
            Check(plan.Blockers.Count == 0, string.Join(' ', plan.Blockers));
            Check(plan.Families.Single(family => family.Family == "ties").Protect.Contains($"entity:{locked.EntityId}"), "locked target survives preview");
            Check(plan.Families.Where(family => family.Selected).All(family => family.Mode == ContentImportMode.Replace), "replacement modes");
            await RejectAsync<InvalidOperationException>(() => UyaConvertedContentImportService.PrepareCommitAsync(request, workspace, raw, prepared, "stale", [], dex));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await RejectAsync<OperationCanceledException>(() => UyaConvertedContentImportService.PrepareCommitAsync(request, workspace, raw, prepared, plan.BatchId, [], dex, cancelled.Token));
            var command = await UyaConvertedContentImportService.PrepareCommitAsync(request, workspace, raw, prepared, plan.BatchId, [], dex);
            Check(workspace.CurrentFingerprint == before, "preparation leaves the project unchanged");
            await RejectAsync<IOException>(() => runtime.CommitContentImportAsync(command with
            {
                FaultInjector = phase => { if (phase == ContentImportCommitPhase.Publishing) throw new IOException("Injected GC publication failure"); },
            }));
            Check(await runtime.ReadWorkspaceAsync(path, (active, _) => Task.FromResult(active.CurrentFingerprint == before)), "failed publication preserves the project");
            await runtime.CommitContentImportAsync(command);
            var imported = await runtime.ReadWorkspaceAsync(path, (active, _) => Task.FromResult(active.Content));
            Check(imported.Entities.Any(entity => entity.EntityId == locked.EntityId)
                && imported.Entities.All(entity => entity.EntityId != oldTie.EntityId && entity.EntityId != oldMoby.EntityId), "replace removes only approved targets");
            Check(imported.Entities.Count(entity => entity.Provenance?.Game == "GC" && entity.Layer == "mobys") == 3,
                "models and model-less GC Mobys retain source provenance");
            var pvar = imported.MobyPVars!.Entries.Single(entry => imported.Entities.Any(entity => entity.EntityId == entry.EntityId && entity.Provenance?.Game == "GC"));
            Check(pvar.Data.AsSpan().SequenceEqual(new byte[] { 1, 0, 0, 0, 0, 0, 0, 0, 0x5a, 0x6b, 0x7c, 0x8d }), "GC PVar bytes remain intact");
            Check(pvar.MobyLinkOffsets.SequenceEqual([0]) && pvar.RelativePointerOffsets.SequenceEqual([4]), "native link and relative-pointer data");
            Check(pvar.References.Count == 1 && pvar.References[0].Reference.FieldKey == "nativeMobyLink[0]", "GC PVars use only native links, never UYA schemas");
            Check(imported.Entities.Any(entity => entity.EntityId == pvar.References[0].Reference.EntityId && entity.Provenance?.Game == "GC"), "native link follows the imported Moby");
            Check(imported.ImportBatches.Single().SourceGame == "GC", "batch provenance");
            var rawId = AssetId.Compute(AssetKind.World, 1, prepared.RawGameplay);
            var rawPath = await runtime.ReadWorkspaceAsync(path, (active, _) => Task.FromResult(active.ResolveAttachedAssetPath(rawId)));
            Check(rawPath is not null && (await File.ReadAllBytesAsync(rawPath)).AsSpan().SequenceEqual(prepared.RawGameplay),
                "original gameplay, orphan PVars and untouched link tables are preserved");
            await runtime.ExecuteAsync(new(Guid.NewGuid().ToString("D"), EditorCommandKind.Undo, []));
            Check(await runtime.ReadWorkspaceAsync(path, (active, _) => Task.FromResult(active.CurrentFingerprint == before)), "one undo restores all replaced families");
            await runtime.ExecuteAsync(new(Guid.NewGuid().ToString("D"), EditorCommandKind.Redo, []));
            await runtime.SaveAsync();
            var reopened = await ForgeProjectWorkspace.OpenAsync(path);
            Check(reopened.Content.ImportBatches.Single().BatchId == plan.BatchId && reopened.Content.MobyPVars!.Entries.Any(entry => entry.Data.SequenceEqual(pvar.Data)), "save and reopen preserve the import");
            Check(reopened.IsAssetReferenced(rawId), "archived source payload remains referenced");
            var collisionRecord = (await UyaBaseLayerStore.InspectAsync(reopened, catalog, BakeLayerId.Collision, default))
                .Manifest!.Layers.Single(layer => layer.Layer == BakeLayerId.Collision);
            var collision = await UyaBaseLayerStore.ComposeCollisionAsync(reopened, catalog, collisionRecord, default);
            Check(collision["collision.bin"].AsSpan().SequenceEqual(donorCollisionBytes),
                "GC replacement validates against the verified target and composes the attached donor collision after reopening");
            var collected = await reopened.CollectUnreferencedAssetsAsync();
            Check(!collected.Contains(rawId) && reopened.ResolveAttachedAssetPath(rawId) is not null,
                "asset collection preserves the original GC gameplay tables");
            var empty = await AssetCatalogStore.OpenReadOnlyAsync(Path.Combine(root, "empty-catalog"));
            var inputs = await UyaStaticLayerStore.CreateBakeInputsAsync(path, empty);
            Check(inputs.Where(input => input.Id is BakeLayerId.Ties or BakeLayerId.Mobys).All(input => input.Blockers!.Count == 0), "native models bake without donor media");
            var bakePlan = BakeLayerGraph.CreatePlan(new(new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"), "translator-1", "baker-1"), inputs);
            var staging = await BakeStagingStore.OpenAsync(path);
            foreach (var layer in new[] { BakeLayerId.Ties, BakeLayerId.Shrubs, BakeLayerId.Mobys })
                await UyaStaticLayerStore.StageAsync(path, empty, staging, bakePlan.Layers.Single(value => value.Layer == layer));
            var inventory = await UyaTextureInventoryService.BuildAsync(path, empty);
            Check(inventory.Textures.Count > 0, "attached GC model textures build an inventory without the donor catalog");
            var gameplayInput = await UyaGameplayLayerStore.CreateBakeInputAsync(path);
            Check(gameplayInput.Blockers!.Count == 0, "imported GC geometry and PVars validate for baking: " + string.Join("; ", gameplayInput.Blockers!));
            Console.WriteLine("GC replacement, raw PVars and links, undo/redo, persistence and native bake checks passed.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static byte[] CreateWad()
    {
        var blocks = new Dictionary<string, byte[]>();
        blocks["level_settings"] = new byte[0x28];
        var ties = new byte[0x10 + 2 * 0x60]; Write(ties, 0, 2);
        for (var index = 0; index < 2; index++) { Write(ties, 0x10 + index * 0x60, 200); Write(ties, 0x60 + index * 0x60, 0x0f); }
        blocks["tie_instances"] = ties;
        var mobys = new byte[0x10 + 3 * 0x88]; Write(mobys, 0, 3);
        for (var index = 0; index < 3; index++)
        {
            var offset = 0x10 + index * 0x88;
            Write(mobys, offset, 0x88); Write(mobys, offset + 0x10, index + 10);
            Write(mobys, offset + 0x28, index == 2 ? 777 : 100 + index);
            Write(mobys, offset + 0x2c, BitConverter.SingleToInt32Bits(1));
            Write(mobys, offset + 0x58, -1); Write(mobys, offset + 0x68, index == 0 ? 0 : -1);
        }
        blocks["moby_instances"] = mobys;
        blocks["pvar_table"] = new byte[16]; Write(blocks["pvar_table"], 4, 12); Write(blocks["pvar_table"], 8, 12); Write(blocks["pvar_table"], 12, 4);
        blocks["pvar_data"] = [1, 0, 0, 0, 0, 0, 0, 0, 0x5a, 0x6b, 0x7c, 0x8d, 0xde, 0xad, 0xbe, 0xef];
        blocks["pvar_moby_links"] = new byte[16]; Write(blocks["pvar_moby_links"], 8, -1); Write(blocks["pvar_moby_links"], 12, -1);
        blocks["pvar_relative_pointers"] = new byte[16]; Write(blocks["pvar_relative_pointers"], 4, 4); Write(blocks["pvar_relative_pointers"], 8, -1); Write(blocks["pvar_relative_pointers"], 12, -1);
        blocks["cuboids"] = new byte[0x90]; Write(blocks["cuboids"], 0, 1);
        foreach (var offset in new[] { 0x10, 0x24, 0x38, 0x4c, 0x50, 0x60, 0x70 }) Write(blocks["cuboids"], offset, BitConverter.SingleToInt32Bits(1));
        var wad = GcContentImportPreviewTests.CreateWad();
        var cursor = GcGameplayLayout.CoreHeaderSize;
        wad.AsSpan(0x1000).Clear();
        foreach (var definition in GcGameplayLayout.Core.Blocks)
            if (blocks.TryGetValue(definition.SemanticName, out var bytes))
            {
                Write(wad, 0x1000 + definition.HeaderOffset, cursor);
                bytes.CopyTo(wad, 0x1000 + cursor); cursor += bytes.Length;
            }
        return wad;
    }

    private static void Write(Span<byte> bytes, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes[offset..], value);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception($"GC replacement: {message}"); }
    private static async Task RejectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}");
    }
}
