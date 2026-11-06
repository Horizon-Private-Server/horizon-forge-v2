using System.Security.Cryptography;
using Forge.Host.Bridge;
using Forge.Host.Domain;
using Forge.Host.Games.DL;
using Forge.Host.Games.UYA;
using Forge.Host.ProtocolTests.Games.UYA;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.LevelAssets;
using RatchetPs2.Core.Moby;
using RatchetPs2.Games.UYA.Gameplay;
using RatchetPs2.Sdk;

namespace Forge.Host.ProtocolTests.Games.DL;

internal static class DlContentImportTests
{
    public static async Task RunAsync()
    {
        var root = Directory.CreateTempSubdirectory("forge-dl-content-test-").FullName;
        try
        {
            var fingerprint = new string('b', 32);
            var catalog = await AssetCatalogStore.OpenAsync(Path.Combine(root, "catalog"));
            foreach (var (kind, classId, model) in new[] {
                (AssetKind.Tie, 200, new byte[0x80]), (AssetKind.Shrub, 300, new byte[0xc0]),
                (AssetKind.Moby, 100, MobyModelPacker.Build(new() { Scale = 1 })) })
            {
                var definition = new byte[kind == AssetKind.Shrub ? 0x30 : 0x20]; System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(definition.AsSpan(4), classId);
                await catalog.PutAsync(kind, 1, CatalogModelCodec.Encode(GameId.DL, definition, model, []), new("dl-test",
                    new("DL", "NTSC-U", "1.00", "level42", "assets/asset_wad.bin", 0, fingerprint), [$"{kind.ToString().ToLowerInvariant()}:0x{classId:X4}"]));
            }
            // Mission class indices overlap core class indices and must not replace core models.
            await catalog.PutAsync(AssetKind.Moby, 1, CatalogModelCodec.Encode(GameId.DL, new byte[0x20], [1, 2, 3], []), new("dl-test",
                new("DL", "NTSC-U", "1.00", "level42", "missions/0000/classes.bin", 0, fingerprint), ["moby:0xFFFF"]));
            var wad = CreateWad();
            var conversion = LevelContentConverter.Convert(GameId.DL, GameId.UYA, wad);
            var skyBytes = conversion.Files.Single(file => file.Path == "assets/asset_wad.bin").Bytes.AsSpan(0x1100, 0x560).ToArray();
            var inventory = LevelContentInventoryReader.ReadConverted(GameId.DL, GameId.UYA, conversion);
            var source = new ContentImportSource(new(true, "DL", "NTSC-U", "1.00", "SCUS-97465", wad.Length, fingerprint, "fixture"),
                Convert.ToHexString(SHA256.HashData(wad)).ToLowerInvariant(), inventory, wad, conversion);
            var path = Path.Combine(root, "project");
            var targetSource = new OpaqueContentSource("UYA", "NTSC-U", "1.00", 41, new string('a', 32));
            var payload = new UyaBaseLayerPayload(BakeLayerId.World, "level-settings.bin", AssetKind.World,
                "gameplay/core/level_settings.bin", 0, new byte[UyaLevelSettingsReader.MinimumSize]);
            var targetPayloads = new[] { payload, new UyaBaseLayerPayload(BakeLayerId.Lighting, "directional-lights.bin", AssetKind.Lighting,
                "gameplay/core/directional_lights.bin", 0, new byte[0x10]),
                new UyaBaseLayerPayload(BakeLayerId.Collision, "collision.bin", AssetKind.Collision,
                    "level_wad/assets/asset_wad.bin", 0, UyaRenderPackageTests.BuildCollisionFixture()) };
            var entries = await catalog.PutManyAsync(UyaBaseLayerService.CreateCatalogPuts(targetPayloads, targetSource, "target-test"));
            await UyaBaseLayerStore.WriteAsync(path, targetSource, targetPayloads, entries);
            var workspace = await ForgeProjectWorkspace.CreateAsync(path, "DL content", new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
                new("UYA", "NTSC-U", "1.00", 41, targetSource.Fingerprint), []);
            var selections = new ContentImportSelection[] { new("ties", ContentImportMode.Replace), new("shrubs", ContentImportMode.Replace),
                new("mobys", ContentImportMode.Replace), new("cuboids", ContentImportMode.Replace), new("directionalLights", ContentImportMode.Replace),
                new("levelSettings", ContentImportMode.Replace), new("occlusion", ContentImportMode.Replace), new("skybox", ContentImportMode.Replace), new("collision", ContentImportMode.Replace) };
            var request = new UyaConvertedContentImportPlanRequest(GameId.DL, path, catalog.RootPath, "fixture.iso", fingerprint, 42, selections);
            var dex = new MobyDexCatalog(UyaMobyDexDataset.Create());
            var prepared = await UyaConvertedContentImportPreparation.CreateAsync(request, source, dex, null, default);
            Check(prepared.Diagnostics.Count == 0, string.Join(' ', prepared.Diagnostics.Values));
            Check(prepared.Source.Occlusion is not null, "world mapping and grid produce an importable native occlusion source");
            var plan = UyaConvertedContentImportService.CreatePlan(request, workspace, source, prepared.Source, prepared.Diagnostics);
            Check(plan.Game == "DL" && plan.Blockers.Count == 0, string.Join(' ', plan.Blockers));
            var settingsOnly = UyaConvertedContentImportService.CreatePlan(request with { Selections = [new("levelSettings", ContentImportMode.Replace)] }, workspace, source, prepared.Source);
            Check(settingsOnly.ReferenceRequirements!.Count == 2, "unselected donor cuboids require mapping choices");
            var occlusionOnly = UyaConvertedContentImportService.CreatePlan(request with { Selections = [new("occlusion", ContentImportMode.Replace)] }, workspace, source, prepared.Source);
            Check(occlusionOnly.Blockers.Count == 0, "occlusion imports independently of scene families");
            ContentImportSourceCache.ValidateIdentity(source.Identity, GameId.DL, "NTSC-U", "1.00", fingerprint);
            Reject<InvalidDataException>(() => ContentImportSourceCache.ValidateIdentity(source.Identity with { Game = "RC1" }, GameId.DL, "NTSC-U", "1.00", fingerprint));
            var originalSkyPlan = UyaConvertedContentImportService.CreatePlan(
                request with { Selections = [new("skybox", ContentImportMode.Replace)] }, workspace, source, prepared.Source, prepared.Diagnostics);
            var before = workspace.CurrentFingerprint;
            await RejectAsync<InvalidOperationException>(() => UyaConvertedContentImportService.PrepareCommitAsync(request, workspace, source, prepared, "stale", [], dex));
            await RejectAsync<OperationCanceledException>(() => UyaConvertedContentImportService.PrepareCommitAsync(request, workspace, source, prepared, plan.BatchId, [], dex, new CancellationToken(true)));
            var command = await UyaConvertedContentImportService.PrepareCommitAsync(request, workspace, source, prepared, plan.BatchId, [], dex);
            Check(workspace.CurrentFingerprint == before, "preparation is read only");
            await using var runtime = new EditorRuntime(transformCapabilityResolver: UyaEditorCapabilities.ResolveTransformCapabilities);
            await runtime.OpenAsync(path, TimeSpan.FromDays(1));
            await runtime.CommitContentImportAsync(command);
            await runtime.ExecuteAsync(new(Guid.NewGuid().ToString("D"), EditorCommandKind.Undo, []));
            Check(await runtime.ReadWorkspaceAsync(path, (active, _) => Task.FromResult(active.CurrentFingerprint == before)), "one undo restores the target");
            await runtime.ExecuteAsync(new(Guid.NewGuid().ToString("D"), EditorCommandKind.Redo, []));
            await runtime.SaveAsync();
            var reopened = await ForgeProjectWorkspace.OpenAsync(path);
            Check(reopened.Content.ImportBatches.Single().SourceGame == "DL", "batch provenance persists");
            Check(reopened.Content.Entities.All(entity => entity.Provenance?.Game == "DL"), "entity provenance persists");
            Check(reopened.Content.Entities.Count(entity => entity.Layer == "ties") == 1 && reopened.Content.Entities.Count(entity => entity.Layer == "shrubs") == 1, "static instances persist");
            Check(reopened.Content.LevelSettings!.Scene!.DeathHeight == -25, "DL scene settings persist");
            Check(reopened.Content.MobyPVars!.Entries[0].Data.SequenceEqual(new byte[] { 1, 0, 0, 0, 0x5a, 0, 0, 0 })
                && reopened.Content.MobyPVars.Entries[0].MobyLinkOffsets.SequenceEqual([0])
                && reopened.Content.MobyPVars.Entries[0].References.Single().Reference.EntityId == reopened.Content.Entities.Single(entity => entity.Layer == "mobys" && entity.Provenance?.SourceIndex == 1).EntityId,
                "raw PVars and Moby links survive import and persistence");
            var importedMoby = reopened.Content.Entities.First(entity => entity.Layer == "mobys");
            Check(UyaMobyPropertyService.Describe(importedMoby)?.Single(property => property.Key == UyaMobyPropertyService.DrawDistance)
                .Value.Integer == 123, "converted DL Mobys expose native editable properties");
            Check(UyaMobyPropertyService.Describe(importedMoby with { Provenance = importedMoby.Provenance! with { Game = "unknown" } }) is null,
                "unsupported Moby provenance remains read only");
            var skyAsset = reopened.Content.Entities.Single(entity => entity.SkyShell is not null).Asset!;
            var savedSkyBytes = await File.ReadAllBytesAsync(reopened.ResolveAttachedAssetPath(skyAsset.Id)!);
            var expectedSky = RatchetPs2.Core.Skyboxes.SkyboxReader.Read(new MemoryStream(skyBytes), GameId.UYA);
            var savedSky = RatchetPs2.Core.Skyboxes.SkyboxReader.Read(new MemoryStream(savedSkyBytes), GameId.UYA);
            Check(savedSky.Textures.Count == expectedSky.Textures.Count && savedSky.Textures.Zip(expectedSky.Textures)
                .All(pair => pair.First.PixelData.SequenceEqual(pair.Second.PixelData) && pair.First.PaletteData.SequenceEqual(pair.Second.PaletteData)),
                "SDK-converted sky textures survive composition and project persistence");
            var archive = reopened.Content.ImportBatches.Single().SourcePayload!;
            Check((await File.ReadAllBytesAsync(reopened.ResolveAttachedAssetPath(archive.Id)!)).SequenceEqual(conversion.RawGameplay), "original core gameplay is archived");
            await UyaBaseLayerStore.ValidateLevelSettingsAsync(reopened, catalog, default);
            var collisions = reopened.Content.Entities.Where(entity => entity.Collision is not null).ToArray();
            var deleted = new[] {
                collisions.Single(entity => entity.Collision is { Kind: ProjectCollisionPieceKind.Solid, SourcePieceIndex: 1 }).EntityId,
                collisions.Single(entity => entity.Collision!.Kind == ProjectCollisionPieceKind.PlayerBarrier).EntityId };
            await runtime.ExecuteAsync(new(Guid.NewGuid().ToString("D"), EditorCommandKind.DeleteEntities, deleted));
            var afterDelete = await runtime.ReadWorkspaceAsync(path, (active, _) => Task.FromResult(active.CreateProjection()));
            Check(!afterDelete.Content.Entities.Any(entity => deleted.Contains(entity.EntityId)), "converted DL solids and barriers can be deleted");
            Check(reopened.Content.Entities.Where(entity => entity.Collision is not null || entity.Geometry is not null || entity.Lighting is not null)
                .All(entity => UyaEditorCapabilities.ResolveTransformCapabilities(entity) != EditorTransformCapabilities.None),
                "converted DL collision, geometry and lighting expose their native transform capabilities");
            Check(UyaEditorCapabilities.ResolveTransformCapabilities(collisions[0] with {
                Provenance = collisions[0].Provenance! with { Game = "unknown" } }) == EditorTransformCapabilities.None,
                "unknown source formats remain read only");
            await runtime.ExecuteAsync(new(Guid.NewGuid().ToString("D"), EditorCommandKind.Undo, []));
            Check(await runtime.ReadWorkspaceAsync(path, (active, _) => Task.FromResult(deleted.All(id => active.Content.Entities.Any(entity => entity.EntityId == id)))),
                "undo restores the deleted DL collision pieces");
            await runtime.ExecuteAsync(new(Guid.NewGuid().ToString("D"), EditorCommandKind.Redo, []));
            await runtime.SaveAsync();
            var edited = await ForgeProjectWorkspace.OpenAsync(path);
            Check(!edited.Content.Entities.Any(entity => deleted.Contains(entity.EntityId)), "collision deletion survives redo and save/reopen");
            var collisionRecord = (await UyaBaseLayerStore.InspectAsync(edited, catalog, BakeLayerId.Collision, default))
                .Manifest!.Layers.Single(layer => layer.Layer == BakeLayerId.Collision);
            var collisionBytes = (await UyaBaseLayerStore.ComposeCollisionAsync(edited, catalog, collisionRecord, default))["collision.bin"];
            var collisionOutput = CollisionConverter.Inspect(collisionBytes, GameId.UYA);
            Check(collisionOutput.Pieces.Count(piece => piece.Kind == CollisionPieceKind.Solid) == 1
                && collisionOutput.Pieces.All(piece => piece.Kind != CollisionPieceKind.PlayerBarrier),
                "native collision output removes the deleted solid and barrier while keeping the remaining solid");
            var missingSound = await catalog.PutAsync(AssetKind.SoundSample, 1, new byte[] { 1, 2, 3 }, new("dl-test",
                new("DL", "NTSC-U", "1.00", "level42", "sounds/bank.bin", 0, fingerprint)));
            File.Delete(catalog.ResolveBlobPath(missingSound.Id)!);
            var damaged = await UyaConvertedContentImportPreparation.CreateAsync(request, source, dex, null, default);
            Check(damaged.Diagnostics.Keys.SequenceEqual(["mobys"]), "missing sound dependencies are scoped to Mobys");
            var skyOnly = request with { Selections = [new("skybox", ContentImportMode.Replace)] };
            var skyPlan = UyaConvertedContentImportService.CreatePlan(skyOnly, workspace, source, damaged.Source, damaged.Diagnostics);
            Check(skyPlan.Blockers.Count == 0, "unrelated missing sound blobs do not block sky imports");
            var damagedPlan = UyaConvertedContentImportService.CreatePlan(request, workspace, source, damaged.Source, damaged.Diagnostics);
            Check(damagedPlan.Families.Single(family => family.Family == "mobys").SourceState == ContentImportSourceState.Malformed,
                "damaged sound dependencies still block the Moby family");
            Check(damagedPlan.BatchId != plan.BatchId, "a catalog change invalidates an existing preview");
            await RejectAsync<InvalidOperationException>(() => UyaConvertedContentImportService.PrepareCommitAsync(
                skyOnly, workspace, source, damaged, originalSkyPlan.BatchId, [], dex));
            Console.WriteLine("DL content conversion, preview/replacement, raw PVars/links, occlusion, undo/redo and persistence passed.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    internal static byte[] CreateWad(bool emptyFixupPadding = false) => File.ReadAllBytes(Path.Combine(
        AppContext.BaseDirectory, "fixtures", emptyFixupPadding ? "dl-empty-fixups.wad" : "dl-content.wad"));

    private static void Check(bool value, string message) { if (!value) throw new Exception("DL content: " + message); }
    private static void Reject<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}"); }
    private static async Task RejectAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}"); }
}
