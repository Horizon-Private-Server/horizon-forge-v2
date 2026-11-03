using Forge.Host.Domain;
using Forge.Host.Games.UYA;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.Moby;
using RatchetPs2.Sdk;

namespace Forge.Host.ProtocolTests.Games.UYA;

internal static class UyaAssetSourceSelectionTests
{
    public static async Task RunAsync(string root)
    {
        var catalog = await AssetCatalogStore.OpenAsync(Path.Combine(root, "source-catalog"));
        var workspace = await ForgeProjectWorkspace.CreateAsync(Path.Combine(root, "source-project"), "Source selection",
            new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"), new("UYA", "NTSC-U", "1.00", 3, new string('a', 32)), []);
        var sourceBank = UyaDlAssetResourceTests.CreateBank(0x21);
        await workspace.SetSoundsAsync(SoundProjectService.CreateBank(GameId.UYA, 3, sourceBank,
            SoundBankReader.Inspect(GameId.UYA, sourceBank)));
        var opaqueSource = new OpaqueContentSource("UYA", "NTSC-U", "1.00", 3, new string('a', 32));
        await OpaqueContentStore.WriteAsync(workspace.RootPath, opaqueSource,
            [new("level/sound-bank", new("level_wad", 0x18, 0, sourceBank.Length, 2048), sourceBank)]);
        var model = CatalogModelCodec.Encode(GameId.DL, new byte[0x20], MobyModelPacker.Build(new() { Scale = 1 }), []);
        const int classId = 0x20fa;
        AssetSource Source(string level) => new("DL", "NTSC-U", "1.00", level, "classes.bin", 0, new string('a', 32));
        var selections = new List<AssetSourceSelection>();
        AssetCatalogEntry? moby = null;
        foreach (var (level, value) in new[] { ("level01", (byte)0x43), ("level42", (byte)0x65) })
        {
            var bank = UyaDlAssetResourceTests.CreateBank(value);
            var inventory = SoundBankReader.Inspect(GameId.DL, bank);
            await catalog.PutManyAsync(CatalogSoundAssets.Create(bank, inventory, Source(level), "test"));
            var source = Source(level) with { MobySounds = CatalogSoundAssets.MobyUse(
                AssetId.Compute(AssetKind.SoundBank, 1, bank), inventory.Programs.ToDictionary(value => value.NativeIndex), classId, [0]) };
            moby = await catalog.PutAsync(AssetKind.Moby, 1, model,
                new("test", source, [$"moby:0x{classId:X4}"], ["vanilla"]));
            selections.Add(AssetSourceSelection.From(source));
        }
        moby = await catalog.PutAsync(AssetKind.Moby, 1, model,
            new("test", Source("level99"), [$"moby:0x{classId:X4}"], ["vanilla"]));
        selections.Add(AssetSourceSelection.From(Source("level99")));
        var baseline = workspace.CurrentFingerprint;
        try
        {
            await UyaAssetPlacementService.CreateAsync(workspace, catalog.RootPath,
                new(moby.Id, AssetKind.Moby, classId, ProjectTransform.Identity, selections[0] with { Level = "level98" }), default);
            throw new Exception("Expected stale source rejection");
        }
        catch (InvalidDataException) { }
        Check(workspace.CurrentFingerprint == baseline, "invalid source selection leaves the project untouched");
        await workspace.SaveAsync();
        await using (var runtime = new EditorRuntime(placementResolver: UyaAssetPlacementService.CreateAsync))
        {
            await runtime.OpenAsync(workspace.RootPath, catalog.RootPath, TimeSpan.Zero);
            var before = (await runtime.GetSnapshotAsync()).SoundRevision;
            await runtime.ExecuteAsync(new(Guid.NewGuid().ToString("D"), EditorCommandKind.CreateEntityFromAsset, [],
                Placement: new(moby.Id, AssetKind.Moby, classId, ProjectTransform.Identity, selections[1])));
            var snapshot = await runtime.GetSnapshotAsync();
            Check(snapshot.SoundRevision != before, "sound imports change the renderer revision");
            await runtime.ExecuteAsync(new(Guid.NewGuid().ToString("D"), EditorCommandKind.SetSelection,
                [snapshot.Entities.Single().EntityId]));
            Check((await runtime.GetSnapshotAsync()).SoundRevision == snapshot.SoundRevision,
                "ordinary scene selection does not invalidate sound previews");
            await runtime.ExecuteAsync(new(Guid.NewGuid().ToString("D"), EditorCommandKind.Undo, []));
            Check((await runtime.GetSnapshotAsync()).SoundRevision == before, "undo restores the original sound revision");
            await runtime.ExecuteAsync(new(Guid.NewGuid().ToString("D"), EditorCommandKind.Redo, []));
            await runtime.SaveAsync();
        }
        workspace = await ForgeProjectWorkspace.OpenAsync(workspace.RootPath);
        var map = workspace.Content.Sounds.MobyRemaps.Single();
        var expectedSound = moby.Sources.Single(value => selections[1].Matches(value)).MobySounds!.Slots.Single().Sound;
        Check(map.Source == selections[1] && map.Slots.Single().Sound?.Id == expectedSound,
            "placement and save/reopen preserve the selected level's sounds for a shared model");
        var preview = await UyaSoundCompositionService.PreparePreviewAsync(workspace, default);
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            UyaSoundCompositionService.PreparePreviewAsync(workspace, default)));
        Check(concurrent.All(value => ReferenceEquals(value, preview)), "unchanged sound state reuses one composed preview bank");
        var revision = SoundWorkspaceService.Revision(workspace.Content.Sounds);
        _ = await UyaAssetPlacementService.CreateAsync(workspace, catalog.RootPath,
            new(moby.Id, AssetKind.Moby, classId, ProjectTransform.Identity, selections[1]), default);
        Check(ReferenceEquals(await UyaSoundCompositionService.PreparePreviewAsync(workspace, default), preview)
            && SoundWorkspaceService.Revision(workspace.Content.Sounds) == revision,
            "placing another copy of the same source retains the audio cache and revision");
        await VerifyRepeatedPlacementAsync(workspace, catalog, moby, classId, selections[1]);
        workspace.Rename("Renamed");
        Check(ReferenceEquals(await UyaSoundCompositionService.PreparePreviewAsync(workspace, default), preview)
            && SoundWorkspaceService.Revision(workspace.Content.Sounds) == revision,
            "unrelated project changes retain the audio cache and revision");
        try
        {
            await UyaAssetPlacementService.CreateAsync(workspace, catalog.RootPath,
                new(moby.Id, AssetKind.Moby, classId, ProjectTransform.Identity, selections[0]), default);
            throw new Exception("Expected active sound variant rejection");
        }
        catch (InvalidDataException) { }
        workspace.RemoveEntity(workspace.Content.Entities.Single().EntityId);
        var second = await UyaAssetPlacementService.CreateAsync(workspace, catalog.RootPath,
            new(moby.Id, AssetKind.Moby, classId, ProjectTransform.Identity, selections[0]), default);
        workspace.AddEntity(second);
        var updated = await UyaSoundCompositionService.PreparePreviewAsync(workspace, default);
        Check(!ReferenceEquals(updated, preview) && SoundWorkspaceService.Revision(workspace.Content.Sounds) != revision,
            "changing a source invalidates the preview cache and renderer sound revision");
        Check(workspace.Content.Sounds.MobyRemaps.Single().Source == selections[0], "reimport uses the newly selected source");
        workspace.RemoveEntity(second.EntityId);
        var silent = await UyaAssetPlacementService.CreateAsync(workspace, catalog.RootPath,
            new(moby.Id, AssetKind.Moby, classId, ProjectTransform.Identity, selections[2]), default);
        workspace.AddEntity(silent);
        await workspace.SaveAsync();
        workspace = await ForgeProjectWorkspace.OpenAsync(workspace.RootPath);
        Check(workspace.Content.Sounds.MobyRemaps.Single() is { Slots.Count: 0, Source.Level: "level99" }
            && await UyaMobySoundImportService.HydrateConvertedAsync(workspace, catalog, default) is null,
            "a deliberately silent source stays silent after reopening and hydration");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await UyaSoundCompositionService.PreparePreviewAsync(workspace, cancelled.Token); throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { }
        Check(await UyaSoundCompositionService.PreparePreviewAsync(workspace, default) is not null,
            "cancellation does not poison the preview cache");
        Console.WriteLine("PASS exact moby sound source selection, persistence, silent sources, revisions, and preview cache invalidation");
    }

    private static async Task VerifyRepeatedPlacementAsync(ForgeProjectWorkspace workspace, AssetCatalogStore catalog,
        AssetCatalogEntry moby, int classId, AssetSourceSelection selection)
    {
        var state = workspace.Content.Sounds;
        var source = moby.Sources.Single(selection.Matches);
        var bankPath = catalog.ResolveBlobPath(source.MobySounds!.Bank)!;
        var heldBank = bankPath + ".held";
        var partialPath = Path.Combine(catalog.BlobRootPath, "active-import.partial");
        await File.WriteAllBytesAsync(partialPath, [1, 2, 3]);
        File.Move(bankPath, heldBank);
        try
        {
            foreach (var selected in new AssetSourceSelection?[] { selection, null })
            {
                var placed = await UyaAssetPlacementService.CreateAsync(workspace, catalog.RootPath,
                    new(moby.Id, AssetKind.Moby, classId, ProjectTransform.Identity, selected), default);
                Check(placed.Source?.ClassId == classId && placed.Asset?.Id == moby.Id
                    && ReferenceEquals(state, workspace.Content.Sounds),
                    "repeat placement retains class and sound state without rereading its source bank");
            }
            Check(File.Exists(partialPath), "read-only placement leaves active catalog imports untouched");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try
            {
                await UyaMobySoundImportService.ImportAsync(workspace, catalog, moby, classId, cancelled.Token, selection);
                throw new Exception("Expected cancelled repeat import");
            }
            catch (OperationCanceledException) { }
        }
        finally { File.Move(heldBank, bankPath); File.Delete(partialPath); }

        var otherSounds = moby.Sources.First(value => value.Level != source.Level && value.MobySounds is not null).MobySounds!;
        var invalidIndex = source.MobySounds with { Slots = source.MobySounds.Slots
            .Select(slot => slot with { NativeProgramIndex = slot.NativeProgramIndex + 1 }).ToArray() };
        foreach (var sounds in new[] { otherSounds, invalidIndex })
        {
            var changedSource = source with { MobySounds = sounds };
            var changed = moby with { Sources = moby.Sources.Select(value => value == source ? changedSource : value).ToArray() };
            try
            {
                await UyaMobySoundImportService.ImportAsync(workspace, catalog, changed, classId, default, selection);
                throw new Exception("Expected changed sound mapping rejection");
            }
            catch (InvalidDataException) { }
        }
        Check(ReferenceEquals(state, workspace.Content.Sounds), "changed mappings and invalid program indices cannot bypass validation");

        var modelPath = workspace.ResolveAttachedAssetPath(workspace.ResolveAssetReference(new(moby.Id, AssetKind.Moby)).Id)
            ?? throw new Exception("Converted model attachment is missing");
        var model = await File.ReadAllBytesAsync(modelPath);
        var corrupt = model.ToArray(); corrupt[^1] ^= 1;
        await File.WriteAllBytesAsync(modelPath, corrupt);
        try
        {
            await UyaAssetPlacementService.CreateAsync(workspace, catalog.RootPath,
                new(moby.Id, AssetKind.Moby, classId, ProjectTransform.Identity, selection), default);
            throw new Exception("Expected converted model integrity rejection");
        }
        catch (InvalidDataException) { }
        finally { await File.WriteAllBytesAsync(modelPath, model); }
        Check(ReferenceEquals(state, workspace.Content.Sounds), "repeat placement still verifies the converted model before importing sounds");
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
