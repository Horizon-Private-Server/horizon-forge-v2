using System.Buffers.Binary;
using Forge.Host.Bridge;
using Forge.Host.Domain;
using Forge.Host.Games.UYA;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.IO;
using RatchetPs2.Core.Moby;
using RatchetPs2.Core.LevelAssets;
using RatchetPs2.Core.Skyboxes;
using RatchetPs2.Core.Sounds;
using RatchetPs2.Core.Wad;
using RatchetPs2.Games.UYA.Moby;
using RatchetPs2.Games.UYA.Level;
using RatchetPs2.Sdk;

namespace Forge.Host.ProtocolTests.Games.UYA;

internal static class UyaDlAssetResourceTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-dl-resources-{Guid.NewGuid():N}");
        try
        {
            var catalog = await AssetCatalogStore.OpenAsync(Path.Combine(root, "catalog"));
            var path = Path.Combine(root, "project");
            var workspace = await ForgeProjectWorkspace.CreateAsync(path, "DL resources",
                new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"), new("UYA", "NTSC-U", "1.00", 3, new string('a', 32)), []);
            var source = new OpaqueContentSource("UYA", "NTSC-U", "1.00", 3, new string('a', 32));
            var sky = EditorRuntimeTests.BuildSkybox();
            var payload = new UyaBaseLayerPayload(BakeLayerId.Sky, "sky.bin", AssetKind.Sky, "assets/sky", 0, sky);
            var baseEntry = (await catalog.PutManyAsync(UyaBaseLayerService.CreateCatalogPuts([payload], source, "test"))).Single();
            await UyaBaseLayerStore.WriteAsync(path, source, [payload], [baseEntry]);
            sky[0] = 42;
            var dlSky = await catalog.PutAsync(AssetKind.Sky, 0, sky,
                new("test", Source(), SkyShellCatalogService.Aliases(sky, GameId.DL), ["vanilla", "game:DL"]));
            var page = await SkyShellCatalogService.QueryAsync(catalog, new(null, "DL", null, null, null, [], null, 64), GameId.UYA);
            Check(page.Items is [{ ShellIndex: 0 }], "DL sky shells appear separately in the explorer");
            var query = new AssetExplorerRequestPayload(catalog.RootPath, AssetExplorerCategoryPayload.SkyShells,
                dlSky.Id.ToString(), null, null, null, null, [], null, 64, "UYA", "NTSC-U", "1.00", 0);
            var explorer = AssetExplorerPayloadCodec.DecodePage(await ProjectBridgeHandlers.HandleAsync(
                new(BridgeMessageKind.Request, BridgeOpcode.QueryAssetExplorer, BridgeErrorCode.None, 1,
                    AssetExplorerPayloadCodec.EncodeRequest(query)), "test", _ => ValueTask.CompletedTask, default));
            Check(explorer.Items.Single().CanPlace, "DL sky shell is draggable into UYA");
            var preview = await UyaAssetPreviewService.PrepareAsync(new(Path.Combine(root, "preview"),
                catalog.RootPath, dlSky.Id, AssetKind.Sky, "UYA", "sky-default", ShellIndex: 0), "test");
            Check(File.Exists(Path.Combine(preview.RootPath, preview.ModelPath)), "DL sky shell preview");

            var targetBank = CreateBank(0x21);
            var baseSounds = SoundProjectService.CreateBank(GameId.UYA, 3, targetBank, SoundBankReader.Inspect(GameId.UYA, targetBank));
            await workspace.SetSoundsAsync(baseSounds);
            await OpaqueContentStore.WriteAsync(path, source,
                [new("level/sound-bank", new("level_wad", 0x18, 0, targetBank.Length, 2048), targetBank)]);
            await workspace.SaveAsync();
            var donorBank = CreateBank(0x43);
            var inventory = SoundBankReader.Inspect(GameId.DL, donorBank);
            var aliases = inventory with { Programs = Enumerable.Range(0, 1500)
                .Select(index => inventory.Programs[0] with { NativeIndex = index }).ToArray() };
            Check(CatalogSoundAssets.Create(donorBank, aliases, Source(), "test").Count(value => value.Kind == AssetKind.Sound) == 1,
                "identical program aliases in one bank do not exhaust catalog source limits");
            await catalog.PutManyAsync(CatalogSoundAssets.Create(donorBank, inventory, Source() with { Archive = "missions/0000/sound.bnk" }, "test"));
            var bankId = AssetId.Compute(AssetKind.SoundBank, 1, donorBank);
            var model = new MobyModel { Scale = 1, Sounds = [new() { Index = 0, MaxRange = 20 }],
                AnimationFormat = MobyAnimationFormat.Compact, SkeletonFormat = MobyAnimationFormat.Compact,
                JointCount = 2, Skeleton = new(), CommonTransforms = new byte[32] };
            model.Skeleton.Bones.Add(new() { Row1 = new() { X = 1 }, Row2 = new() { Y = 1 }, Row3 = new() { Z = 1 } });
            model.Skeleton.Bones.Add(new() { Row1 = new() { X = 1, W = -10 },
                Row2 = new() { Y = 1, W = -20 }, Row3 = new() { Z = 1, W = -30 } });
            model.CommonTransforms[12] = 0xff;
            BinaryPrimitives.WriteSingleLittleEndian(model.CommonTransforms.AsSpan(16), 10);
            BinaryPrimitives.WriteSingleLittleEndian(model.CommonTransforms.AsSpan(20), 20);
            BinaryPrimitives.WriteSingleLittleEndian(model.CommonTransforms.AsSpan(24), 30);
            var original = MobyModelPacker.Build(model);
            var moby = await catalog.PutAsync(AssetKind.Moby, 1, CatalogModelCodec.Encode(GameId.DL, new byte[0x20], original, []),
                new("test", Source() with { MobySounds = CatalogSoundAssets.MobyUse(bankId,
                    inventory.Programs.ToDictionary(value => value.NativeIndex), 0x1234, [0]) },
                    ["moby:0x1234"], ["vanilla", "game:DL"]));
            var audio = await SoundWorkspaceService.RenderCatalogPreviewAsync(catalog.RootPath, bankId, "DL", 0, true, default);
            Check(audio.Pcm16.Length > 0, "DL catalog sound preview produces PCM");
            await using (var runtime = new EditorRuntime(placementResolver: UyaAssetPlacementService.CreateAsync,
                skyShellCommandExecutor: UyaSkyShellEditorService.ExecuteAsync,
                mobyPropertyResolver: UyaMobyPropertyService.Describe,
                mobyPropertyCommandExecutor: UyaMobyPropertyService.ExecuteAsync))
            {
                await runtime.OpenAsync(path, catalog.RootPath, TimeSpan.Zero);
                await runtime.ExecuteAsync(new(Guid.NewGuid().ToString("D"), EditorCommandKind.AddSkyShellFromAsset, [],
                    SkyShellSource: new(dlSky.Id, 0)));
                Check(await runtime.ReadWorkspaceAsync(path, (value, _) => Task.FromResult(
                    value.Content.Assets.Any(asset => asset.Id == dlSky.Id)), default), "sky source is attached");
                await runtime.ExecuteAsync(new(Guid.NewGuid().ToString("D"), EditorCommandKind.Undo, []));
                Check(await runtime.ReadWorkspaceAsync(path, (value, _) => Task.FromResult(value.Content.AssetOverrides.Count == 0), default),
                    "sky conversion undo restores metadata");
                await runtime.ExecuteAsync(new(Guid.NewGuid().ToString("D"), EditorCommandKind.Redo, []));
                await runtime.ExecuteAsync(new(Guid.NewGuid().ToString("D"), EditorCommandKind.CreateEntityFromAsset, [],
                    Placement: new(moby.Id, AssetKind.Moby, 0x1234, ProjectTransform.Identity)));
                Check((await runtime.GetSnapshotAsync()).Entities.Single(value => value.Layer == "mobys")
                    .MobyProperties?.Count == 14, "newly converted DL mobys expose their UYA instance data");
                Check(await runtime.ReadWorkspaceAsync(path, (value, _) => Task.FromResult(
                    value.Content.Sounds.MobyRemaps.Single().ClassId == 0x1234
                    && value.Content.Sounds.Programs.Any(program => program.Provenance.Game == "DL")), default),
                    "converted class retains source-class sound mappings");
                await runtime.ExecuteAsync(new(Guid.NewGuid().ToString("D"), EditorCommandKind.Undo, []));
                Check(await runtime.ReadWorkspaceAsync(path, (value, _) => Task.FromResult(value.Content.Sounds.Programs.Count == 1
                    && value.Content.Sounds.MobyRemaps.Count == 0), default), "placement undo restores sounds");
                await runtime.ExecuteAsync(new(Guid.NewGuid().ToString("D"), EditorCommandKind.Redo, []));
                await runtime.SaveAsync();
            }
            workspace = await ForgeProjectWorkspace.OpenAsync(path);
            var soundPreviewBank = await UyaSoundCompositionService.PreparePreviewAsync(workspace, default);
            var soundWorkspace = await SoundWorkspaceService.ReadAsync(workspace, default, soundPreviewBank);
            Check(soundWorkspace.Programs.Select(value => value.NativeIndex).Distinct().Count() == 2,
                "project sound indices stay unique across target and DL source banks");
            var dlProgram = soundWorkspace.Programs.Single(value => value.SourceGame == "DL");
            Check(dlProgram.MobyDependents.Single().ClassId == 0x1234, "project sound dependents retain the DL class identity");
            var projectAudio = await SoundWorkspaceService.RenderPreviewAsync(workspace, dlProgram.NativeIndex, true, default, soundPreviewBank);
            Check(projectAudio.Pcm16.SequenceEqual(audio.Pcm16), "DL project sound preview retains source audio");
            var composed = await UyaSkyShellEditorService.ComposeAsync(workspace, catalog, workspace.Content.Entities, default);
            Check(composed.Bytes.Length > 0, "converted sky shell composition survives save/reopen");
            var reference = new ProjectAssetReference(moby.Id, AssetKind.Moby);
            var expected = workspace.ResolveAssetReference(reference);
            var native = UyaCanonicalAssetCodec.Decode(await workspace.ReadAttachedAssetVerifiedAsync(expected, ForgeProjectWorkspace.MaxAttachedAssetBytes));
            using var legacyStream = new MemoryStream(original);
            var silent = UyaMobyConverter.ConvertFromCompact(MobyModelReader.Read(legacyStream,
                new() { AnimationFormat = MobyAnimationFormat.Compact }), [], preserveSounds: false, normalizeRuntimeMetadata: false);
            var attachments = new ProjectAssetAttachmentCollector();
            var previous = attachments.Attach(AssetKind.Moby, 1,
                UyaCanonicalAssetCodec.Encode(native.DefinitionBytes, silent, []), moby.Id);
            await workspace.CommitAssetsAsync(workspace.Content with
            {
                AssetOverrides = workspace.Content.AssetOverrides.Select(value => value.Source == reference
                    ? value with { Replacement = previous } : value).ToArray(),
            }, attachments.Attachments);
            await workspace.SetSoundsAsync(baseSounds);
            Check(await UyaMobySoundImportService.HydrateConvertedAsync(workspace, catalog, default) is not null,
                "existing DL mobys acquire newly extracted sound banks");
            Check(await UyaConvertedAssetRepairService.HydrateAsync(workspace, default) is not null
                && workspace.ResolveAssetReference(reference) == expected, "old silent conversions regain their sound definitions");
            using var fallbackStream = new MemoryStream(original);
            var oldFallback = UyaMobyConverter.ConvertFromCompact(MobyModelReader.Read(fallbackStream,
                new() { AnimationFormat = MobyAnimationFormat.Compact }), [], rebuildSkeletonFallback: false);
            var fallbackAttachments = new ProjectAssetAttachmentCollector();
            var oldFallbackReference = fallbackAttachments.Attach(AssetKind.Moby, 1,
                UyaCanonicalAssetCodec.Encode(native.DefinitionBytes, oldFallback, native.Textures), moby.Id);
            await workspace.CommitAssetsAsync(workspace.Content with
            {
                AssetOverrides = workspace.Content.AssetOverrides.Select(value => value.Source == reference
                    ? value with { Replacement = oldFallbackReference } : value).ToArray(),
            }, fallbackAttachments.Attachments);
            var entities = workspace.Content.Entities.ToArray();
            var sounds = workspace.Content.Sounds;
            Check(await UyaConvertedAssetRepairService.HydrateAsync(workspace, default) is not null
                && workspace.ResolveAssetReference(reference) == expected, "existing conversions regain skeleton LOD fallback data");
            Check(workspace.Content.Entities.SequenceEqual(entities) && workspace.Content.Sounds == sounds,
                "skeleton repair preserves placements and sound mappings");
            Check(await UyaMobySoundImportService.HydrateConvertedAsync(workspace, catalog, default) is null,
                "sound hydration is idempotent");
            await workspace.SaveAsync();
            workspace = await ForgeProjectWorkspace.OpenAsync(path);
            Check(workspace.Content.Sounds.MobyRemaps.Single().Slots.Single().Sound is not null, "sound mappings persist");
            await VerifySoundPackingAsync(workspace, targetBank);
            await VerifyPreviewBudgetAsync(root);
            await UyaAssetSourceSelectionTests.RunAsync(root);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static async Task VerifyPreviewBudgetAsync(string root)
    {
        const int spuBytes = 2 * 1024 * 1024;
        var sourceBank = new byte[((132 + spuBytes + 2047) / 2048) * 2048];
        CreateBank(0x21).CopyTo(sourceBank, 0);
        Write(sourceBank, 20, spuBytes);
        Write(sourceBank, 24 + 0x28, spuBytes);
        Write(sourceBank, 24 + 0x2c, spuBytes);
        var workspace = await ForgeProjectWorkspace.CreateAsync(Path.Combine(root, "preview-budget"), "Preview budget",
            new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"), new("UYA", "NTSC-U", "1.00", 3, new string('a', 32)), []);
        var state = SoundProjectService.CreateBank(GameId.UYA, 3, sourceBank, SoundBankReader.Inspect(GameId.UYA, sourceBank));
        var donorBytes = CreateBank(0x43);
        var donorBank = SoundBankReader.Inspect(GameId.DL, donorBytes);
        var donor = SoundProjectService.CreateBank(GameId.DL, 1, donorBytes, donorBank);
        var use = CatalogSoundAssets.MobyUse(AssetId.Compute(AssetKind.SoundBank, 1, donorBytes),
            donorBank.Programs.ToDictionary(value => value.NativeIndex), 0x1234, [0]);
        var imported = UyaMobySoundImportService.Prepare(state, donor, donorBytes, donorBank, use, 0x1234,
            new(AssetId.Compute(AssetKind.Moby, 1, [1]), AssetKind.Moby));
        await workspace.SetSoundsAsync(imported.State, imported.Attachments);
        var source = new OpaqueContentSource("UYA", "NTSC-U", "1.00", 3, new string('a', 32));
        await OpaqueContentStore.WriteAsync(workspace.RootPath, source,
            [new("level/sound-bank", new("level_wad", 0x18, 0, sourceBank.Length, 2048), sourceBank)]);
        var fingerprint = workspace.CurrentFingerprint;
        var preview = await UyaSoundCompositionService.PreparePreviewAsync(workspace, default);
        var inventory = await SoundWorkspaceService.ReadAsync(workspace, default, preview);
        var program = inventory.Programs.Single(value => value.SourceGame == "DL");
        var audio = await SoundWorkspaceService.RenderPreviewAsync(workspace, program.NativeIndex, true, default, preview);
        Check(System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                SoundBankReader.RenderPreview(GameId.DL, donorBytes, 0).Pcm16.AsSpan()).SequenceEqual(audio.Pcm16)
            && workspace.CurrentFingerprint == fingerprint,
            "inspecting and playing imported sounds beyond the SPU packing budget preserves source audio and project state");
        var header = new byte[0xe0];
        Write(header, 0x18, 1); Write(header, 0x1c, 0xc0); Write(header, 0xc4, 0x1234);
        header = SoundBankComposer.ComposeRemap(GameId.UYA, header, new(1, new Dictionary<int, IReadOnlyList<int>>(), [], []));
        try
        {
            await UyaSoundCompositionService.ComposeAsync(workspace, sourceBank, header, header, default);
            throw new Exception("Expected SPU packing budget rejection");
        }
        catch (InvalidDataException error) when (error.Message.Contains("PS2 SPU budget", StringComparison.Ordinal)) { }
        Check(workspace.CurrentFingerprint == fingerprint, "SPU packing rejection preserves project state");
    }

    private static async Task VerifySoundPackingAsync(ForgeProjectWorkspace workspace, byte[] sourceBank)
    {
        byte[] Header(params int[] classes)
        {
            var bytes = new byte[0xc0 + classes.Length * 0x20];
            Write(bytes, 0x18, classes.Length); Write(bytes, 0x1c, 0xc0);
            for (var index = 0; index < classes.Length; index++) Write(bytes, 0xc4 + index * 0x20, classes[index]);
            return SoundBankComposer.ComposeRemap(GameId.UYA, bytes, new(1, new Dictionary<int, IReadOnlyList<int>>(), [], []));
        }
        var sourceHeader = Header(100);
        var targetHeader = Header(100, 0x1234);
        var map = workspace.Content.Sounds.MobyRemaps.Single();
        await workspace.SetSoundsAsync(workspace.Content.Sounds with
        {
            MobyRemaps = [new(0, 100, null, 0, new(0, 0), []), map with { DefinitionIndex = 1 },
                map with { DefinitionIndex = 2, ClassId = 0x2079, Moby = null }],
        });
        var entity = workspace.Content.Entities.Single(value => value.Layer == "mobys");
        var fingerprint = workspace.CurrentFingerprint;
        var composed = await UyaSoundCompositionService.ComposeAsync(
            workspace, sourceBank, sourceHeader, targetHeader, default);
        var header = SoundBankComposer.ComposeRemap(GameId.UYA, targetHeader, composed.Remap);
        VerifyNativeSoundMap(header, 0x1234, [1]);
        var remap = SoundBankReader.InspectRemap(GameId.UYA, header, 2);
        Check(remap.Mobys.Single(value => value.ClassId == 0x1234).Slots.Single().NativeProgramIndex == 1
            && remap.Mobys.Single(value => value.ClassId == 100).Slots.Count == 0,
            "packing keeps the imported DL sound on its final class and retains model-only base classes");
        Check(!composed.Remap.MobySlotsByClassId.ContainsKey(0x2079)
            && workspace.CurrentFingerprint == fingerprint, "packing omits orphan sound maps without changing the project");
        workspace.RemoveEntity(entity.EntityId);
        await workspace.SaveAsync();
        workspace = await ForgeProjectWorkspace.OpenAsync(workspace.RootPath);
        composed = await UyaSoundCompositionService.ComposeAsync(workspace, sourceBank, sourceHeader, sourceHeader, default);
        Check(composed.Remap.MobySlotsByClassId.Keys.SequenceEqual([100]) && !composed.RemapChanged
            && workspace.Content.Sounds.MobyRemaps.Count == 3, "deleted placements retain sound data but emit no orphan remaps after reopening");
        Check(!composed.BankChanged && composed.BankBytes.SequenceEqual(sourceBank)
            && composed.Plan.AppendedPrograms.Count == 0 && composed.Plan.AppendedSamples.Count == 0,
            "deleted placements pack neither their imported programs nor their samples");
        var preview = await UyaSoundCompositionService.PreparePreviewAsync(workspace, default);
        Check(preview is not null && preview.State.Programs.Count == 2,
            "deleted placement sounds remain available in the project preview inventory");
        workspace.AddEntity(entity);
        composed = await UyaSoundCompositionService.ComposeAsync(workspace, sourceBank, sourceHeader, targetHeader, default);
        Check(composed.Remap.MobySlotsByClassId[0x1234].Single() == 1, "restored placement regains its imported sound mapping");
        await VerifySoundDependencyPackingAsync(workspace, sourceBank, sourceHeader, targetHeader);
    }

    private static void VerifyNativeSoundMap(byte[] headerBytes, int classId, IReadOnlyList<int> programs)
    {
        var header = LevelAssetReader.ReadHeader(headerBytes);
        var definition = LevelAssetReader.ReadModelDefinitions(headerBytes, header.MobyModelOffset, header.MobyModelCount)
            .Single(value => value.ModelId == classId);
        var remap = headerBytes.AsSpan(header.SoundRemapOffset);
        var offset = BinaryPrimitives.ReadInt16LittleEndian(remap[(8 + definition.Index * 4)..]);
        var count = BinaryPrimitives.ReadInt16LittleEndian(remap[(10 + definition.Index * 4)..]);
        Check(count == programs.Count + 1 && BinaryPrimitives.ReadInt32LittleEndian(remap[offset..]) + 1 == 0,
            $"native UYA loader selects the primary bank for class 0x{classId:X4}");
        for (var slot = 0; slot < programs.Count; slot++)
            Check(BinaryPrimitives.ReadInt32LittleEndian(remap[(offset + 4 + slot * 4)..]) == programs[slot],
                $"native UYA loader resolves class 0x{classId:X4} sound slot {slot}");
    }

    private static async Task VerifySoundDependencyPackingAsync(
        ForgeProjectWorkspace workspace, byte[] sourceBank, byte[] sourceHeader, byte[] targetHeader)
    {
        var donorSource = CreateBank(0x43);
        var unusedSource = CreateBank(0x65);
        var tone = new SoundBankToneInput(100, 127, 60, 0, 0, 0, 127, 0, 0, 0, 0, 0, 0, "child");
        var donorBytes = SoundBankComposer.Compose(GameId.DL, donorSource,
        [
            new("root", 127, 1, 0, 1, 0, [new(2, 0, 0, 0, 0, new byte[16], null, "child")]),
            new("child", 127, 1, 0, 1, 0, [new(1, 0, 0, 0, 0, new byte[24], tone, null)]),
            new("unused", 127, 1, 0, 1, 0, [new(1, 0, 0, 0, 0, new byte[24], tone with { SampleKey = "unused" }, null)]),
        ], [new("child", donorSource.AsSpan(132, 16).ToArray()), new("unused", unusedSource.AsSpan(132, 16).ToArray())]).BankBytes;
        var donorBank = SoundBankReader.Inspect(GameId.DL, donorBytes);
        var donor = SoundProjectService.CreateBank(GameId.DL, 1, donorBytes, donorBank);
        var state = SoundProjectService.CreateBank(GameId.UYA, 3, sourceBank, SoundBankReader.Inspect(GameId.UYA, sourceBank));
        var bankId = AssetId.Compute(AssetKind.SoundBank, 1, donorBytes);
        foreach (var (classId, programIndex) in new[] { (0x1234, 0), (0x2079, 2) })
        {
            var plan = UyaMobySoundImportService.Prepare(state, donor, donorBytes, donorBank,
                new(bankId, [new(0, programIndex, donor.Programs[programIndex].Asset!.Id, true, null)]), classId,
                new(AssetId.Compute(AssetKind.Moby, 1, BitConverter.GetBytes(classId)), AssetKind.Moby));
            await workspace.SetSoundsAsync(plan.State, plan.Attachments);
            state = plan.State;
        }
        var fingerprint = workspace.CurrentFingerprint;
        var composed = await UyaSoundCompositionService.ComposeAsync(workspace, sourceBank, sourceHeader, targetHeader, default);
        var bank = SoundBankReader.Inspect(GameId.UYA, composed.BankBytes);
        Check(bank.Programs.Count == 3 && bank.Samples.Count == 2
            && bank.Programs.Any(value => value.ContentId == donor.Programs[0].SourceContentId)
            && bank.Programs.Any(value => value.ContentId == donor.Programs[1].SourceContentId)
            && !bank.Programs.Any(value => value.ContentId == donor.Programs[2].SourceContentId),
            "packing retains transitive child sounds and shared samples while omitting the deleted class's exclusive sound");
        Check(workspace.CurrentFingerprint == fingerprint, "sound dependency pruning preserves project state");
        await workspace.SetSoundsAsync(state with
        {
            GlobalRemaps = [new(0, 2, donor.Programs[2].Asset, new(0, 0), null)],
        });
        composed = await UyaSoundCompositionService.ComposeAsync(workspace, sourceBank, sourceHeader, targetHeader, default);
        bank = SoundBankReader.Inspect(GameId.UYA, composed.BankBytes);
        Check(bank.Programs.Count == 4 && bank.Samples.Count == 3
            && bank.Programs[composed.Remap.GlobalSlots.Single()].ContentId == donor.Programs[2].SourceContentId,
            "a global remap retains a sound even when its moby was deleted");
        var currentMap = state.MobyRemaps.First() with
        {
            RawListOffset = 44,
            ConfigurationRange = new(264, 4),
            Slots = [new(0, -1, null, new(304, 4), null),
                state.MobyRemaps.First().Slots[0] with { Slot = 1, SourceRange = new(308, 4) }],
        };
        await workspace.SetSoundsAsync(state with
        {
            SourceRemapRange = new(256, 100),
            MobyRemaps = [currentMap with { Slots = [new(0, -1, null, new(300, 4), null),
                .. currentMap.Slots.Select(slot => slot with { Slot = slot.Slot + 1 })] }],
        });
        Check(await UyaMobySoundImportService.HydrateLegacyRemapsAsync(workspace, default) is not null
            && workspace.Content.Sounds.MobyRemaps.Single().Slots.SequenceEqual(currentMap.Slots),
            "legacy repair removes only the bank selector and retains a real null first sound slot");
        Check(await UyaMobySoundImportService.HydrateLegacyRemapsAsync(workspace, default) is null,
            "legacy sound-slot repair is idempotent");
        await workspace.SaveAsync();
        workspace = await ForgeProjectWorkspace.OpenAsync(workspace.RootPath);
        Check(workspace.Content.Sounds.MobyRemaps.Single().Slots.SequenceEqual(currentMap.Slots),
            "corrected sound-slot numbering survives save and reopen");
    }

    public static async Task QualifySoundPackingAsync(string sourceProject, string scratch, string catalogRoot, string sourceIso)
    {
        if (Directory.Exists(scratch)) throw new InvalidOperationException("A new scratch project is required.");
        Directory.CreateDirectory(scratch);
        File.Copy(Path.Combine(sourceProject, "forge-project.json"), Path.Combine(scratch, "forge-project.json"));
        foreach (var directory in new[] { "assets", "content" })
        foreach (var file in Directory.EnumerateFiles(Path.Combine(sourceProject, directory), "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(scratch, Path.GetRelativePath(sourceProject, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
        var workspace = await ForgeProjectWorkspace.OpenAsync(scratch);
        if (await UyaMobySoundImportService.HydrateLegacyRemapsAsync(workspace, default) is { } soundRepair)
        {
            await workspace.SaveAsync();
            Console.WriteLine(soundRepair.Message);
        }
        var originalEntities = workspace.Content.Entities;
        var repair = await UyaConvertedAssetRepairService.HydrateAsync(workspace, default);
        if (repair is not null)
        {
            Check(workspace.Content.Entities.SequenceEqual(originalEntities), "runtime metadata repair preserves all retail instances");
            await workspace.SaveAsync();
            Console.WriteLine(repair.Message);
        }
        var fingerprint = workspace.CurrentFingerprint;
        var catalog = await AssetCatalogStore.OpenReadOnlyAsync(catalogRoot);
        var context = new BakeFingerprintContext(workspace.Manifest.Target, "sound-packing-qualification", "sound-packing-qualification");
        var bake = await UyaBakeService.BakeAsync(scratch, catalog, context);
        Check(bake.Succeeded, "retail scratch bake failed: " + string.Join(" | ", bake.Validation.Diagnostics.Select(value => value.Cause)));
        using var iso = File.OpenRead(sourceIso);
        var source = LevelArchiveReader.ExtractPrimary(GameId.UYA, iso, workspace.Manifest.BaseLevel.Level);
        var packed = await UyaLevelPackService.PackAsync(scratch, catalog, source, context, applyGeneratedOcclusion: false);
        Check(packed.Succeeded && packed.OutputBytes is not null,
            "retail scratch pack failed: " + string.Join(" | ", packed.Diagnostics.Select(value => value.Cause)));
        var files = UyaLevelWadUnpacker.Unpack(packed.OutputBytes!).Files;
        var bankBytes = files.Single(value => value.Path == "level_wad/sound.bnk").Bytes;
        var bank = SoundBankReader.Inspect(GameId.UYA, bankBytes);
        var header = files.Single(value => value.Path == "assets/asset_header.bin").Bytes;
        UyaMobyClassLookupTests.Verify(
            UyaLevelWadUnpacker.Unpack(source).Files.Single(value => value.Path == "code/code.bin").Bytes,
            files.Single(value => value.Path == "code/code.bin").Bytes, [0x2079, 0x24f9]);
        await VerifyPackedMobyModelsAsync(workspace, header, files.Single(value => value.Path == "assets/asset_wad.bin").Bytes);
        var remap = SoundBankReader.InspectRemap(GameId.UYA, header, bank.Programs.Count);
        var definitions = LevelAssetReader.ReadHeader(header);
        var classes = LevelAssetReader.ReadModelDefinitions(header, definitions.MobyModelOffset, definitions.MobyModelCount)
            .Select(value => value.ModelId).ToHashSet();
        foreach (var map in workspace.Content.Sounds.MobyRemaps)
        {
            if (!classes.Contains(map.ClassId)) continue;
            var actual = remap.Mobys.Single(value => value.ClassId == map.ClassId);
            if (actual.Slots.Count > 0)
                VerifyNativeSoundMap(header, map.ClassId, actual.Slots.Select(slot => slot.NativeProgramIndex).ToArray());
            Check(actual.Slots.Count == map.Slots.Count, $"retail class {map.ClassId:X4} retains its sound slots");
            foreach (var slot in map.Slots.Where(value => value.Sound is not null))
            {
                var program = workspace.Content.Sounds.Programs.First(value => value.Asset == slot.Sound);
                Check(bank.Programs[actual.Slots[slot.Slot].NativeProgramIndex].ContentId == program.SourceContentId,
                    $"retail class {map.ClassId:X4} slot {slot.Slot} retains its original program");
            }
        }
        Check((await ForgeProjectWorkspace.OpenAsync(scratch)).CurrentFingerprint == fingerprint, "packing preserves the retail project content");
        Console.WriteLine($"Retail bake and full archive pack passed: {classes.Count} Moby definitions, {bank.Programs.Count} programs; all retained sound slots verified.");
        Console.WriteLine($"Packed sound bank: {bankBytes.Length} bytes; sample allocation: {bank.SampleDataProvenance.Length} bytes.");
    }

    private static async Task VerifyPackedMobyModelsAsync(ForgeProjectWorkspace workspace, byte[] headerBytes, byte[] assetWad)
    {
        if (BinaryMagic.IsWad(assetWad)) assetWad = WadCompression.Decompress(assetWad);
        var header = LevelAssetReader.ReadHeader(headerBytes);
        var definitions = LevelAssetReader.ReadModelDefinitions(headerBytes, header.MobyModelOffset, header.MobyModelCount);
        var offsets = LevelAssetReader.CollectKnownAssetOffsets(header, assetWad.Length, definitions,
            LevelAssetReader.ReadModelDefinitions(headerBytes, header.TieModelOffset, header.TieModelCount),
            LevelAssetReader.ReadShrubDefinitions(headerBytes, header.ShrubModelOffset, header.ShrubModelCount));
        foreach (var allocation in workspace.Content.CustomClasses.Allocations.Where(value => value.Asset.Kind == AssetKind.Moby
            && value.Provenance.Origin == ProjectCustomClassOrigin.ConvertedAsset))
        {
            var definition = definitions.SingleOrDefault(value => value.ModelId == allocation.ClassId);
            if (definition is null) continue;
            var native = UyaCanonicalAssetCodec.Decode(await workspace.ReadAttachedAssetVerifiedAsync(
                workspace.ResolveAssetReference(allocation.Asset), ForgeProjectWorkspace.MaxAttachedAssetBytes));
            var packedBytes = LevelAssetReader.ReadAssetSlice(assetWad, definition.ModelOffset, offsets);
            using var nativeInput = new MemoryStream(native.ModelBytes);
            using var packedInput = new MemoryStream(packedBytes);
            var expected = MobyModelReader.Read(nativeInput);
            var packed = MobyModelReader.Read(packedInput);
            UyaMobyLodTests.Verify(packedBytes);
            Check(packed.HighLodMeshCount == expected.HighLodMeshCount && packed.LowLodMeshCount == expected.LowLodMeshCount
                && packed.JointCount == expected.JointCount, "packed DL moby retains all LODs and joints");
            Check(packedBytes.AsSpan(packed.SkeletonOffset, packed.JointCount * 64)
                .SequenceEqual(native.ModelBytes.AsSpan(expected.SkeletonOffset, expected.JointCount * 64)),
                "packed DL moby retains the converted inverse binds and skeleton fallback metadata");
            var expectedMeshes = expected.MeshTable?.Entries ?? [];
            var packedMeshes = packed.MeshTable?.Entries ?? [];
            Check(packedMeshes.Count == expectedMeshes.Count && packedMeshes.Zip(expectedMeshes)
                .All(pair => pair.First.VertexData.SequenceEqual(pair.Second.VertexData)),
                "packed DL moby retains high and low LOD vertex and skinning packets");
            var original = CatalogModelCodec.Decode(await workspace.ReadAttachedAssetVerifiedAsync(
                allocation.Asset, ForgeProjectWorkspace.MaxAttachedAssetBytes));
            using var sourceInput = new MemoryStream(original.ModelBytes);
            var sourceModel = MobyModelReader.Read(sourceInput, new() { AnimationFormat = MobyAnimationFormat.Compact });
            Check(packed.Sequences.Count == sourceModel.Sequences.Count && packed.Sequences.Zip(sourceModel.Sequences)
                .All(pair => pair.First.Sound == pair.Second.Sound && pair.First.FrameCount == pair.Second.FrameCount
                    && pair.First.Triggers.Select(trigger => (trigger.Unknown00, trigger.Unknown02))
                        .SequenceEqual(pair.Second.Triggers.Select(trigger => (trigger.Unknown00, trigger.Unknown02))))
                && packed.SoundCount == sourceModel.SoundCount
                && packedBytes.AsSpan(packed.SoundDefOffset, packed.SoundCount * 32)
                    .SequenceEqual(original.ModelBytes.AsSpan(sourceModel.SoundDefOffset, sourceModel.SoundCount * 32)),
                "packed DL moby retains source animation sound IDs, frame counts, trigger times and sound definitions");
            Console.WriteLine($"Packed DL Moby 0x{allocation.ClassId:X4}: {packed.HighLodMeshCount} high / {packed.LowLodMeshCount} low LOD packets and {packed.JointCount} joints verified.");
        }
    }

    internal static async Task QualifySkiesAsync(AssetCatalogStore original, AssetCatalogStore catalog,
        ForgeProjectWorkspace workspace, byte[] headerBytes, byte[] assetWad, string sourceGame = "DL")
    {
        var header = LevelAssetReader.ReadHeader(headerBytes);
        var offsets = LevelAssetReader.CollectKnownAssetOffsets(header, assetWad.Length,
            LevelAssetReader.ReadModelDefinitions(headerBytes, header.MobyModelOffset, header.MobyModelCount),
            LevelAssetReader.ReadModelDefinitions(headerBytes, header.TieModelOffset, header.TieModelCount),
            LevelAssetReader.ReadShrubDefinitions(headerBytes, header.ShrubModelOffset, header.ShrubModelCount));
        var baseSky = LevelAssetReader.ReadAssetSlice(assetWad, header.SkyOffset, offsets);
        var source = new OpaqueContentSource("UYA", "NTSC-U", "1.00", 3, new string('a', 32));
        var payload = new UyaBaseLayerPayload(BakeLayerId.Sky, "sky.bin", AssetKind.Sky, "assets/sky", 0, baseSky);
        var native = (await catalog.PutManyAsync(UyaBaseLayerService.CreateCatalogPuts([payload], source, "qualification"))).Single();
        await UyaBaseLayerStore.WriteAsync(workspace.RootPath, source, [payload], [native]);
        var skies = original.Query(new(Kind: AssetKind.Sky, Game: sourceGame));
        foreach (var sky in skies)
        {
            var bytes = await File.ReadAllBytesAsync(original.ResolveBlobPath(sky.Id)!);
            var origin = sky.Sources.First(value => value.Game == sourceGame);
            var converted = SkyboxComposer.Convert(Enum.Parse<GameId>(sourceGame), GameId.UYA, bytes, int.Parse(origin.Level.AsSpan(5)));
            using var stream = new MemoryStream(converted);
            var read = SkyboxReader.Read(stream, GameId.UYA);
            Check(read.Shells.Count > 0, "retail DL sky shells survive UYA conversion");
            var preview = await UyaAssetPreviewService.PrepareAsync(new(Path.Combine(workspace.RootPath, "sky-previews"),
                original.RootPath, sky.Id, AssetKind.Sky, "UYA", "sky-default", 0), "qualification");
            Check(File.Exists(Path.Combine(preview.RootPath, preview.ModelPath)), "retail DL sky-shell preview");
            if (workspace.Content.Entities.Any(value => value.SkyShell is not null)) continue;
            var entry = await catalog.PutAsync(AssetKind.Sky, 0, bytes, new(sky.ImporterVersion, origin, sky.Aliases, sky.Tags));
            await UyaSkyShellEditorService.ExecuteAsync(workspace, catalog.RootPath, new(Guid.NewGuid().ToString("D"),
                EditorCommandKind.AddSkyShellFromAsset, [], SkyShellSource: new(entry.Id, 0)), default);
        }
        var composed = await UyaSkyShellEditorService.ComposeAsync(workspace, catalog, workspace.Content.Entities, default);
        using var output = new MemoryStream(composed.Bytes);
        Check(SkyboxReader.Read(output, GameId.UYA).Shells.Count == 1, "retail DL shell composes into a native UYA sky");
        await workspace.SaveAsync();
        var reopened = await ForgeProjectWorkspace.OpenAsync(workspace.RootPath);
        await UyaSkyShellEditorService.ComposeAsync(reopened, catalog, reopened.Content.Entities, default);
        Console.WriteLine($"Qualified {skies.Count} unique {sourceGame} skies: source previews, UYA conversion and native composition/save/reopen.");
    }

    private static AssetSource Source() => new("DL", "NTSC-U", "1.00", "level01", "assets/asset_wad.bin", 0, new string('b', 32));

    internal static byte[] CreateBank(byte samples)
    {
        var bytes = new byte[2048];
        Write(bytes, 0, 3); Write(bytes, 4, 2); Write(bytes, 8, 24); Write(bytes, 12, 108);
        Write(bytes, 16, 132); Write(bytes, 20, 16);
        var block = bytes.AsSpan(24);
        Write(block, 0, 0x6b6c4253); Write(block, 4, 3); Write(block, 8, 4); Write(block, 12, 0x574144);
        foreach (var offset in new[] { 0x16, 0x18, 0x1a }) BinaryPrimitives.WriteInt16LittleEndian(block[offset..], 1);
        Write(block, 0x1c, 64); Write(block, 0x20, 76); Write(block, 0x28, 16); Write(block, 0x2c, 16); Write(block, 0x34, 84);
        block[64] = 127; block[65] = 1; block[68] = 1; block[69] = 1;
        Write(block, 76, 0x01000000);
        block[84] = 100; block[85] = 127; block[86] = 60; block[91] = 127;
        bytes[133] = 1; bytes.AsSpan(134, 14).Fill(samples);
        return bytes;
    }

    private static void Write(Span<byte> bytes, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes[offset..], value);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
