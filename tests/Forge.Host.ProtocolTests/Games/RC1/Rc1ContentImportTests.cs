using System.Security.Cryptography;
using Forge.Host.Domain;
using Forge.Host.Games.RC1;
using Forge.Host.Games.UYA;
using Forge.Host.ProtocolTests.Games.UYA;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.Textures.Pif;
using RatchetPs2.Games.UYA.Gameplay;
using RatchetPs2.Sdk;

namespace Forge.Host.ProtocolTests.Games.RC1;

internal static class Rc1ContentImportTests
{
    public static async Task RunAsync()
    {
        var root = Directory.CreateTempSubdirectory("forge-rc1-content-test-").FullName;
        try
        {
            var fingerprint = new string('b', 32);
            var catalog = await AssetCatalogStore.OpenAsync(Path.Combine(root, "catalog"));
            using (var iso = new MemoryStream(Rc1AssetImportTests.CreateIso()))
                await Rc1AssetImportService.ImportValidatedAsync(iso, new(GameId.RC1, "fixture.iso", catalog.RootPath,
                    fingerprint, "1.00", "rc1-content-test"));
            catalog = await AssetCatalogStore.OpenAsync(catalog.RootPath);
            var oldTie = catalog.Query(new(Kind: AssetKind.Tie, Game: "RC1")).Single();
            var tieModel = CatalogModelCodec.Decode(await File.ReadAllBytesAsync(catalog.ResolveBlobPath(oldTie.Id)!));
            var baseTexture = PifReader.Read(tieModel.Textures.Single().PifBytes);
            var mipTexture = PifWriter.Write(new(baseTexture.Header, baseTexture.Encoding,
                baseTexture.PaletteData, baseTexture.PixelData,
                [new byte[16 * 16], new byte[8 * 8]]));
            await catalog.PutAsync(AssetKind.Tie, oldTie.CanonicalFormatVersion,
                CatalogModelCodec.Encode(GameId.RC1, tieModel.DefinitionBytes, tieModel.ModelBytes,
                    [tieModel.Textures.Single() with { PifBytes = mipTexture }]),
                new(Rc1AssetImportService.ImporterVersionPrefix + "test", oldTie.Sources.Single(value => value.Level == "level00"), oldTie.Aliases));
            var donorBankBytes = UyaDlAssetResourceTests.CreateBank(0x43);
            var donorBank = SoundBankReader.Inspect(GameId.RC1, donorBankBytes);
            var mobyAsset = catalog.Query(new(Kind: AssetKind.Moby, Game: "RC1")).Single();
            var mobySource = mobyAsset.Sources.Single(value => value.Level == "level00");
            await catalog.PutManyAsync(CatalogSoundAssets.Create(donorBankBytes, donorBank, mobySource, "rc1-content-test"));
            await catalog.PutAsync(AssetKind.Moby, mobyAsset.CanonicalFormatVersion,
                await File.ReadAllBytesAsync(catalog.ResolveBlobPath(mobyAsset.Id)!), new("rc1-content-test", mobySource with
                {
                    MobySounds = CatalogSoundAssets.MobyUse(AssetId.Compute(AssetKind.SoundBank, 1, donorBankBytes),
                        donorBank.Programs.ToDictionary(value => value.NativeIndex), 0x1234, [0]),
                }, mobyAsset.Aliases));
            var wad = CreateWad();
            var conversion = LevelContentConverter.Convert(GameId.RC1, GameId.UYA, wad);
            var source = new ContentImportSource(new(true, "RC1", "NTSC-U", "1.00", "SCUS-97199", wad.Length, fingerprint, "fixture"),
                Convert.ToHexString(SHA256.HashData(wad)).ToLowerInvariant(), LevelContentInventoryReader.Read(GameId.RC1, wad), wad);
            var path = Path.Combine(root, "project");
            var targetSource = new OpaqueContentSource("UYA", "NTSC-U", "1.00", 41, new string('a', 32));
            var payload = new UyaBaseLayerPayload(BakeLayerId.World, "level-settings.bin", AssetKind.World,
                "gameplay/core/level_settings.bin", 0, new byte[UyaLevelSettingsReader.MinimumSize]);
            var entries = await catalog.PutManyAsync(UyaBaseLayerService.CreateCatalogPuts([payload], targetSource, "target-test"));
            await UyaBaseLayerStore.WriteAsync(path, targetSource, [payload], entries);
            var workspace = await ForgeProjectWorkspace.CreateAsync(path, "RC1 content", new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
                new("UYA", "NTSC-U", "1.00", 41, targetSource.Fingerprint), []);
            var targetBank = UyaDlAssetResourceTests.CreateBank(0x21);
            await workspace.SetSoundsAsync(SoundProjectService.CreateBank(GameId.UYA, 41, targetBank,
                SoundBankReader.Inspect(GameId.UYA, targetBank)));
            await workspace.SaveAsync();
            var selections = new ContentImportSelection[] { new("ties", ContentImportMode.Replace), new("shrubs", ContentImportMode.Replace),
                new("mobys", ContentImportMode.Replace), new("cuboids", ContentImportMode.Replace), new("levelSettings", ContentImportMode.Replace) };
            var request = new UyaConvertedContentImportPlanRequest(GameId.RC1, path, catalog.RootPath, "fixture.iso", fingerprint, 0, selections);
            var dex = new MobyDexCatalog(UyaMobyDexDataset.Create());
            var prepared = await UyaConvertedContentImportPreparation.CreateAsync(request, source, dex, null, default);
            Check(prepared.Diagnostics.Count == 0, string.Join(' ', prepared.Diagnostics.Values));
            var nativeTie = prepared.Catalog.Query(new(Kind: AssetKind.Tie, Game: "UYA", Level: "level00")).Single();
            var nativeTieModel = UyaCanonicalAssetCodec.Decode(await AssetCatalogBlobReader.ReadVerifiedAsync(prepared.Catalog, nativeTie, ForgeProjectWorkspace.MaxAttachedAssetBytes, default));
            Check(PifReader.Read(nativeTieModel.Textures.Single().PifBytes).Header.MipLevels == 3,
                "old and current RC1 extractions resolve to one native model with authored mipmaps");
            var plan = UyaConvertedContentImportService.CreatePlan(request, workspace, source, prepared.Source, prepared.Diagnostics);
            Check(plan.Game == "RC1" && plan.Blockers.Count == 0 && (plan.ReferenceRequirements ?? []).Count == 0,
                "preview identifies RC1 and automatically maps selected donor cuboids");
            var settingsOnly = UyaConvertedContentImportService.CreatePlan(request with { Selections = [new("levelSettings", ContentImportMode.Replace)] },
                workspace, source, prepared.Source, prepared.Diagnostics);
            Check(settingsOnly.ReferenceRequirements!.Count == 2 && settingsOnly.ReferenceRequirements.All(value => value.TargetKind == ProjectEntityKind.Cuboid),
                "skipped donor cuboids require explicit map or clear choices");
            var clearChoices = settingsOnly.ReferenceRequirements.Select(value => new ContentImportReferenceChoice(
                value.OwnerSourceIndex, value.FieldKey, value.SourceValue, ContentImportReferenceAction.Clear)).ToArray();
            var cleared = await UyaConvertedContentImportService.PrepareCommitAsync(
                request with { Selections = [new("levelSettings", ContentImportMode.Replace)] }, workspace, source, prepared,
                settingsOnly.BatchId, clearChoices, dex);
            Check(cleared.Content.LevelSettings!.Scene!.ShipCameraCuboidStart.SourceValue == -1,
                "settings-only import honors explicit clear choices");
            var before = workspace.CurrentFingerprint;
            await RejectAsync<InvalidOperationException>(() => UyaConvertedContentImportService.PrepareCommitAsync(request, workspace, source, prepared, "stale", [], dex));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await RejectAsync<OperationCanceledException>(() => UyaConvertedContentImportService.PrepareCommitAsync(request, workspace, source, prepared, plan.BatchId, [], dex, cancelled.Token));
            var command = await UyaConvertedContentImportService.PrepareCommitAsync(request, workspace, source, prepared, plan.BatchId, [], dex);
            Check(workspace.CurrentFingerprint == before, "preparation does not mutate target state");
            await using var runtime = new EditorRuntime();
            await runtime.OpenAsync(path, TimeSpan.FromDays(1));
            await runtime.CommitContentImportAsync(command);
            await runtime.ExecuteAsync(new(Guid.NewGuid().ToString("D"), EditorCommandKind.Undo, []));
            Check(await runtime.ReadWorkspaceAsync(path, (active, _) => Task.FromResult(active.CurrentFingerprint == before)), "one undo restores every family");
            await runtime.ExecuteAsync(new(Guid.NewGuid().ToString("D"), EditorCommandKind.Redo, []));
            await runtime.SaveAsync();
            var reopened = await ForgeProjectWorkspace.OpenAsync(path);
            Check(reopened.Content.ImportBatches.Single().SourceGame == "RC1", "batch provenance survives save/reopen");
            Check(reopened.Content.Entities.Single(entity => entity.Layer == "ties").TieLighting!.AmbientRgbas.SequenceEqual(prepared.Source.Entities.Single(entity => entity.Layer == "ties").TieLighting!.AmbientRgbas),
                "native ambient header and all 64 colors survive import, undo/redo and save/reopen");
            Check(reopened.Content.Sounds.Programs.Count == 2 && reopened.Content.Sounds.MobyRemaps.Single().Slots.Count == 1,
                "RC1 Moby sound dependencies import from the native source bank without a UYA donor sound inventory");
            Check(reopened.Content.Entities.All(entity => entity.Provenance?.Game == "RC1"), "imported entity provenance");
            var scene = reopened.Content.LevelSettings!.Scene!;
            Check(scene.DeathHeight == -25 && scene.ShipPosition == new ProjectVector3(1, 2, 3)
                && reopened.Content.Entities.Single(entity => entity.EntityId == scene.ShipCameraCuboidStart.Reference.EntityId).Geometry?.Cuboid is not null,
                "RC1 scene settings and camera references persist");
            Check(UyaEditorCapabilities.ResolveTransformCapabilities(reopened.Content.Entities.Single(entity => entity.Geometry?.Cuboid is not null))
                != EditorTransformCapabilities.None, "imported RC1 geometry exposes transform controls");
            var pvars = reopened.Content.MobyPVars!;
            Check(pvars.Entries[0].Data.SequenceEqual(new byte[] { 1, 0, 0, 0, 0x5a, 0, 0, 0 })
                && pvars.Entries[0].MobyLinkOffsets.SequenceEqual([0])
                && pvars.Entries[0].References.Single().Reference.EntityId == reopened.Content.Entities.Single(entity => entity.Provenance?.SourceIndex == 1 && entity.Layer == "mobys").EntityId,
                "raw PVars and native Moby links persist without UYA behavior schemas");
            var archive = reopened.Content.ImportBatches.Single().SourcePayload!;
            Check((await File.ReadAllBytesAsync(reopened.ResolveAttachedAssetPath(archive.Id)!)).SequenceEqual(conversion.RawGameplay), "original RC1 gameplay archive is retained");
            await UyaBaseLayerStore.ValidateLevelSettingsAsync(reopened, catalog, default);
            await VerifyOversizedTieAsync(root, tieModel, oldTie.Sources.Single(value => value.Level == "level00"), source, dex);
            Console.WriteLine("RC1 content preview, replacement, geometry/settings mappings, raw PVars, undo/redo and persistence passed.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task VerifyOversizedTieAsync(string root, CatalogModel model, AssetSource assetSource,
        ContentImportSource source, MobyDexCatalog dex)
    {
        var catalog = await AssetCatalogStore.OpenAsync(Path.Combine(root, "oversized-catalog"));
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "rc1-oversized-tie.bin"));
        await catalog.PutAsync(AssetKind.Tie, 1, CatalogModelCodec.Encode(GameId.RC1, model.DefinitionBytes, bytes, model.Textures),
            new(Rc1AssetImportService.ImporterVersionPrefix + "test", assetSource, ["tie:4660"]));
        var workspace = await ForgeProjectWorkspace.CreateAsync(Path.Combine(root, "oversized-project"), "Oversized Tie",
            new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"), new("UYA", "NTSC-U", "1.00", 41, new string('a', 32)), []);
        await UyaStaticLayerStore.WriteSourceAsync(workspace.RootPath, new("UYA", "NTSC-U", "1.00", 41, new string('a', 32)),
            [new(BakeLayerId.Ties, 0x60, [0, 0, 0], []), new(BakeLayerId.Shrubs, 0x70, [0, 0, 0], []),
                new(BakeLayerId.Mobys, 0x88, [0, 0, 0], [])]);
        var request = new UyaConvertedContentImportPlanRequest(GameId.RC1, workspace.RootPath, catalog.RootPath,
            "fixture.iso", assetSource.Fingerprint, 0, [new("ties", ContentImportMode.Replace)]);
        var prepared = await UyaConvertedContentImportPreparation.CreateAsync(request, source, dex, null, default);
        Check(!prepared.Diagnostics.ContainsKey("ties"), "oversized model does not block the Tie family");
        var parts = prepared.SplitTieClasses[0x1234];
        Check(parts.Count > 1 && parts.Distinct().Count() == parts.Count, "split parts receive unique classes");
        var plan = UyaConvertedContentImportService.CreatePlan(request, workspace, source, prepared.Source, prepared.Diagnostics,
            warnings: prepared.Warnings);
        Check(plan.Blockers.Count == 0 && plan.Families.Single(family => family.Family == "ties").Add.Count == parts.Count
            && plan.Warnings.Any(warning => warning.Contains("split")), "preview counts all native pieces and explains the split");
        var again = await UyaConvertedContentImportPreparation.CreateAsync(request, source, dex, null, default);
        var repeatPlan = UyaConvertedContentImportService.CreatePlan(request, workspace, source, again.Source, again.Diagnostics,
            warnings: again.Warnings);
        Check(plan.BatchId == repeatPlan.BatchId, "cached parts produce a stable plan");
        var before = workspace.CurrentFingerprint;
        var command = await UyaConvertedContentImportService.PrepareCommitAsync(request, workspace, source, prepared, plan.BatchId, [], dex);
        Check(command.Content.Entities.Count(entity => entity.Layer == "ties") == parts.Count,
            "replacement imports every piece without omitting the original model");
        Check(command.Content.Entities.Where(entity => entity.Layer == "ties").All(entity =>
            entity.TieLighting!.AmbientRgbas.SequenceEqual(prepared.Source.Entities.First(value => value.Layer == "ties").TieLighting!.AmbientRgbas)),
            "all split instances retain source lighting");
        await using var runtime = new EditorRuntime();
        await runtime.OpenAsync(workspace.RootPath, TimeSpan.FromDays(1));
        await runtime.CommitContentImportAsync(command);
        await runtime.ExecuteAsync(new(Guid.NewGuid().ToString("D"), EditorCommandKind.Undo, []));
        Check(await runtime.ReadWorkspaceAsync(workspace.RootPath, (active, _) => Task.FromResult(active.CurrentFingerprint == before)),
            "one undo restores the project before the split import");
        await runtime.ExecuteAsync(new(Guid.NewGuid().ToString("D"), EditorCommandKind.Redo, []));
        await runtime.SaveAsync();
        var reopened = await ForgeProjectWorkspace.OpenAsync(workspace.RootPath);
        Check(reopened.Content.Entities.Count(entity => entity.Layer == "ties") == parts.Count
            && reopened.Content.Entities.Where(entity => entity.Layer == "ties").All(entity => reopened.ResolveAttachedAssetPath(entity.Asset!.Id) is not null),
            "every part survives redo, save and reopen with attached model data");
        var empty = await AssetCatalogStore.OpenReadOnlyAsync(Path.Combine(root, "empty-split-catalog"));
        var inputs = await UyaStaticLayerStore.CreateBakeInputsAsync(workspace.RootPath, empty);
        Check(inputs.Single(input => input.Id == BakeLayerId.Ties).Blockers!.Count == 0,
            "split models bake from project attachments without donor assets: "
                + string.Join("; ", inputs.Single(input => input.Id == BakeLayerId.Ties).Blockers!));
        var bake = BakeLayerGraph.CreatePlan(new(new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"), "translator-1", "baker-1"), inputs);
        var staging = await BakeStagingStore.OpenAsync(workspace.RootPath);
        await UyaStaticLayerStore.StageAsync(workspace.RootPath, empty, staging, bake.Layers.Single(layer => layer.Layer == BakeLayerId.Ties));
    }

    internal static byte[] CreateWad() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "rc1-content.wad"));

    private static void Check(bool value, string message) { if (!value) throw new Exception("RC1 content: " + message); }
    private static async Task RejectAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}"); }
}
