using System.Buffers.Binary;
using Forge.Host.Bridge;
using Forge.Host.Domain;
using Forge.Host.Games.UYA;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.IO;
using RatchetPs2.Core.Moby;
using RatchetPs2.Core.LevelAssets;
using RatchetPs2.Core.Textures.Palettes;
using RatchetPs2.Core.Textures.Pif;
using RatchetPs2.Core.Ties;
using RatchetPs2.Core.Wad;
using RatchetPs2.Sdk;
using RatchetPs2.Games.UYA.Gameplay;
using RatchetPs2.Games.UYA.Level;

namespace Forge.Host.ProtocolTests.Games.UYA;

internal static class UyaConvertedAssetPlacementTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-converted-placement-{Guid.NewGuid():N}");
        try
        {
            var catalog = await AssetCatalogStore.OpenAsync(Path.Combine(root, "catalog"));
            var sources = new List<AssetCatalogEntry>();
            foreach (var kind in new[] { AssetKind.Tie, AssetKind.Shrub, AssetKind.Moby })
            {
                var model = kind switch
                {
                    AssetKind.Tie => new byte[0xa0],
                    AssetKind.Shrub => new byte[0xc0],
                    _ => MobyModelPacker.Build(new() { Scale = 1 }),
                };
                if (kind == AssetKind.Tie)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(model.AsSpan(0x34), 0x80);
                    BinaryPrimitives.WriteInt16LittleEndian(model.AsSpan(0x38), 2);
                    BinaryPrimitives.WriteInt16LittleEndian(model.AsSpan(0x3a), 4);
                }
                var bytes = CatalogModelCodec.Encode(GameId.DL, new byte[kind == AssetKind.Shrub ? 0x30 : 0x20], model, []);
                sources.Add(await catalog.PutAsync(kind, 1, bytes, Metadata(kind)));
            }
            var collision = UyaCustomTieClassRegistry.AllocateConverted(ProjectCustomClassRegistry.Empty,
                AssetKind.Tie, sources[0].Id, 0x1234, new HashSet<int> { 0x9000 });
            Check(collision.Allocations.Single().ClassId == 0x9001, "conversion avoids occupied native classes");
            var projectPath = Path.Combine(root, "project");
            await ForgeProjectWorkspace.CreateAsync(projectPath, "Converted assets", new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
                new("UYA", "NTSC-U", "1.00", 3, new string('a', 32)), []);
            await UyaStaticLayerStore.WriteSourceAsync(projectPath, new("UYA", "NTSC-U", "1.00", 3, new string('a', 32)),
            [
                new(BakeLayerId.Ties, UyaTieInstancesReader.RecordSize, [0, 0, 0], []),
                new(BakeLayerId.Shrubs, UyaShrubInstancesReader.RecordSize, [0, 0, 0], []),
                new(BakeLayerId.Mobys, UyaMobyInstancesReader.RecordSize, [0, 0, 0], []),
            ]);
            await using (var runtime = new EditorRuntime(placementResolver: UyaAssetPlacementService.CreateAsync))
            {
                await runtime.OpenAsync(projectPath, catalog.RootPath, TimeSpan.Zero);
                foreach (var entry in sources)
                {
                    var page = await QueryAsync(catalog.RootPath, entry);
                    Check(page.Items.Single().CanPlace, $"{entry.Kind} conversion is draggable");
                    var placed = await runtime.ExecuteAsync(Place(entry));
                    var entity = placed.Entities.Single(value => value.EntityId == placed.Selection.Single());
                    Check(entity.SourceClassId == (entry.Kind == AssetKind.Moby ? 0x1234 : 0x9000)
                        && entity.Asset!.Id == entry.Id, "converted class and source identity");
                    Check(!entity.State.MissingAsset, "converted source is resolved");
                    await runtime.ExecuteAsync(new(Guid.NewGuid().ToString("D"), EditorCommandKind.Undo, []));
                    Check(await runtime.ReadWorkspaceAsync(projectPath, (workspace, _) => Task.FromResult(
                        !workspace.Content.CustomClasses.Allocations.Any(value => value.Asset.Id == entry.Id)
                        && !workspace.Content.AssetOverrides.Any(value => value.Source.Id == entry.Id)), default), "undo restores conversion metadata");
                    await runtime.ExecuteAsync(new(Guid.NewGuid().ToString("D"), EditorCommandKind.Redo, []));
                    await runtime.ExecuteAsync(Place(entry));
                }
                Check(await runtime.ReadWorkspaceAsync(projectPath, (workspace, _) => Task.FromResult(
                    workspace.Content.CustomClasses.Allocations.Count == 3 && workspace.Content.AssetOverrides.Count == 3), default),
                    "repeated drops reuse conversions and classes");
                var before = await runtime.ReadWorkspaceAsync(projectPath, (workspace, _) => Task.FromResult(workspace.CurrentFingerprint), default);
                var broken = await catalog.PutAsync(AssetKind.Tie, 1,
                    CatalogModelCodec.Encode(GameId.DL, new byte[0x20], [1], []), Metadata(AssetKind.Tie));
                try { await runtime.ExecuteAsync(Place(broken)); throw new Exception("Expected conversion rejection"); }
                catch (InvalidDataException) { }
                Check(await runtime.ReadWorkspaceAsync(projectPath, (workspace, _) => Task.FromResult(workspace.CurrentFingerprint), default) == before, "failed conversion preserves project state");
                await runtime.SaveAsync();
            }
            var reopened = await ForgeProjectWorkspace.OpenAsync(projectPath);
            Check(reopened.Content.Entities.Count == 6 && reopened.Content.CustomClasses.Allocations.Count == 3, "save/reopen placement");
            foreach (var source in sources)
            {
                var resolved = reopened.ResolveAsset(new(source.Id, source.Kind), catalog)!;
                Check(resolved.ProjectAttached && resolved.Effective.Id != source.Id, "native converted attachment");
                var bytes = await File.ReadAllBytesAsync(resolved.Path);
                Check(CatalogModelCodec.Decode(bytes).Game == GameId.UYA, "UYA native canonical envelope");
                Check(reopened.Content.Assets.Single(value => value.Id == resolved.Effective.Id).ParentId == source.Id,
                    "converted attachment retains source ancestry");
            }
            await VerifyPreviousConversionRepairAsync(reopened, catalog, sources.Single(value => value.Kind == AssetKind.Tie));
            var inputs = await UyaStaticLayerStore.CreateBakeInputsAsync(reopened, catalog, default);
            Check(inputs.All(input => input.Blockers?.Count is null or 0), "converted static layers can bake: "
                + string.Join("; ", inputs.SelectMany(input => input.Blockers ?? [])));
            Check(inputs.All(input => input.AssetIds.Count == 2), "bake fingerprints include source and converted blobs");
            var plan = BakeLayerGraph.CreatePlan(new(reopened.Manifest.Target, "test", "test"), inputs);
            var staging = await BakeStagingStore.OpenAsync(projectPath);
            foreach (var layer in UyaStaticLayerSchema.Layers)
                await UyaStaticLayerStore.StageAsync(reopened, catalog, staging, plan.Layers.Single(value => value.Layer == layer), default);
            var inventory = await UyaTextureInventoryService.BuildAsync(reopened, catalog, default);
            Check(inventory.Textures.Count == 0, "converted layers enter texture inventory");
            await VerifyMipRepairAsync(root, catalog, sources);
            foreach (var fixture in sources.Where(value => value.Kind is AssetKind.Moby or AssetKind.Shrub))
                await VerifyLinearTextureRepairAsync(root, catalog, fixture);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static async Task VerifyLinearTextureRepairAsync(string root, AssetCatalogStore catalog, AssetCatalogEntry fixture)
    {
        var workspace = await ForgeProjectWorkspace.CreateAsync(Path.Combine(root, $"linear-texture-repair-{fixture.Kind}"), "Linear texture repair",
            new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"), new("UYA", "NTSC-U", "1.00", 3, new string('a', 32)), []);
        var pif = PifWriter.Write(PifWriter.CreateIndexed8(16, 16, new byte[1024], new byte[256], [new byte[64], new byte[16], new byte[4]]));
        var parsed = PifReader.Read(pif);
        var material = PifWriter.Write(new PifTextureData(parsed.Header, parsed.Encoding, parsed.PaletteData,
            parsed.PixelData, parsed.MipPixelData.Take(2).ToArray()));
        var embedded = pif.ToArray(); BinaryPrimitives.WriteInt32LittleEndian(embedded.AsSpan(4), 6643361);
        var source = CatalogModelCodec.Decode(await File.ReadAllBytesAsync(catalog.ResolveBlobPath(fixture.Id)!));
        var sourceTextures = new List<FrontendAssetTexture> { new(0, embedded) };
        if (fixture.Kind == AssetKind.Shrub) sourceTextures.Add(new(1, embedded));
        var entry = await catalog.PutAsync(fixture.Kind, 1,
            CatalogModelCodec.Encode(GameId.DL, source.DefinitionBytes, source.ModelBytes, sourceTextures), Metadata(fixture.Kind));
        workspace.AddEntity(await UyaAssetPlacementService.CreateAsync(workspace, catalog.RootPath,
            new(entry.Id, entry.Kind, 0x1234, ProjectTransform.Identity), default));
        var reference = new ProjectAssetReference(entry.Id, entry.Kind);
        var expected = workspace.ResolveAssetReference(reference);
        var native = UyaCanonicalAssetCodec.Decode(await workspace.ReadAttachedAssetVerifiedAsync(
            expected, ForgeProjectWorkspace.MaxAttachedAssetBytes));
        Check(native.Textures.Single(value => value.Role == 0).PifBytes.SequenceEqual(material),
            "new linear mission placements have valid PIF allocations and the supported material mip count");
        if (fixture.Kind == AssetKind.Shrub)
            Check(native.Textures.Single(value => value.Role == 1).PifBytes.SequenceEqual(pif),
                "shrub billboard placement retains its supported fourth level");
        var attachments = new ProjectAssetAttachmentCollector();
        var previous = attachments.Attach(fixture.Kind, 1,
            UyaCanonicalAssetCodec.Encode(native.DefinitionBytes, native.ModelBytes, sourceTextures), entry.Id);
        await workspace.CommitAssetsAsync(workspace.Content with
        {
            AssetOverrides = workspace.Content.AssetOverrides.Select(value => value.Source == reference
                ? value with { Replacement = previous } : value).ToArray(),
        }, attachments.Attachments);
        var entities = workspace.Content.Entities.ToArray();
        Check(await UyaConvertedAssetRepairService.HydrateAsync(workspace, default) is not null
            && workspace.ResolveAssetReference(reference) == expected, "existing linear mission imports regain valid PIF allocations");
        Check(workspace.Content.Entities.SequenceEqual(entities), "linear texture repair preserves placements");
        await workspace.SaveAsync();
        workspace = await ForgeProjectWorkspace.OpenAsync(workspace.RootPath);
        Check(await UyaConvertedAssetRepairService.HydrateAsync(workspace, default) is null,
            "linear texture allocation repair persists and is idempotent");
    }

    private static async Task VerifyMipRepairAsync(string root, AssetCatalogStore catalog, IReadOnlyList<AssetCatalogEntry> fixtures)
    {
        var workspace = await ForgeProjectWorkspace.CreateAsync(Path.Combine(root, "mip-repair"), "Mip repair",
            new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"), new("UYA", "NTSC-U", "1.00", 3, new string('a', 32)), []);
        var sourcePif = PifWriter.Write(PifWriter.CreateIndexed8(32, 32, new byte[1024], new byte[1024],
            [Enumerable.Range(0, 256).Select(index => (byte)index).ToArray(),
                Enumerable.Range(0, 64).Select(index => (byte)(index * 3)).ToArray()], isSwizzled: true));
        var expected = new Dictionary<ProjectAssetReference, ProjectAssetReference>();
        foreach (var fixture in fixtures)
        {
            var source = CatalogModelCodec.Decode(await File.ReadAllBytesAsync(catalog.ResolveBlobPath(fixture.Id)!));
            var entry = await catalog.PutAsync(fixture.Kind, 1, CatalogModelCodec.Encode(GameId.DL,
                source.DefinitionBytes, source.ModelBytes, [new(0, sourcePif)]), Metadata(fixture.Kind));
            workspace.AddEntity(await UyaAssetPlacementService.CreateAsync(workspace, catalog.RootPath,
                new(entry.Id, entry.Kind, 0x1234, ProjectTransform.Identity), default));
            var reference = new ProjectAssetReference(entry.Id, entry.Kind);
            var correct = workspace.ResolveAssetReference(reference);
            expected.Add(reference, correct);
            var native = UyaCanonicalAssetCodec.Decode(await workspace.ReadAttachedAssetVerifiedAsync(
                correct, ForgeProjectWorkspace.MaxAttachedAssetBytes, default));
            var texture = PifReader.Read(native.Textures.Single().PifBytes);
            Check(texture.MipPixelData.Count == 2 && texture.MipPixelData[1].SequenceEqual(PifReader.Read(sourcePif).MipPixelData[1]),
                $"new {entry.Kind} placement preserves its smallest linear mip");
            var previousPif = PifWriter.Write(new PifTextureData(texture.Header, texture.Encoding,
                texture.PaletteData, texture.PixelData, [texture.MipPixelData[0]]));
            var attachments = new ProjectAssetAttachmentCollector();
            var previous = attachments.Attach(entry.Kind, 1, UyaCanonicalAssetCodec.Encode(
                native.DefinitionBytes, native.ModelBytes, [new(0, previousPif)]), entry.Id);
            await workspace.CommitAssetsAsync(workspace.Content with
            {
                AssetOverrides = workspace.Content.AssetOverrides.Select(value => value.Source == reference
                    ? value with { Replacement = previous } : value).ToArray(),
            }, attachments.Attachments);
        }
        var entities = workspace.Content.Entities.ToArray();
        var diagnostic = await UyaConvertedAssetRepairService.HydrateAsync(workspace, default);
        Check(diagnostic?.Message.Contains("3 DL asset(s)", StringComparison.Ordinal) == true,
            "existing converted tie, shrub and moby textures are repaired together");
        Check(expected.All(pair => workspace.ResolveAssetReference(pair.Key) == pair.Value), "repair restores exact native mip chains");
        Check(workspace.Content.Entities.SequenceEqual(entities), "mip repair preserves instances and model classes");
        await workspace.SaveAsync();
        workspace = await ForgeProjectWorkspace.OpenAsync(workspace.RootPath);
        Check(await UyaConvertedAssetRepairService.HydrateAsync(workspace, default) is null, "mip repair persists and is idempotent");
    }

    private static async Task VerifyPreviousConversionRepairAsync(
        ForgeProjectWorkspace workspace, AssetCatalogStore catalog, AssetCatalogEntry source)
    {
        var reference = new ProjectAssetReference(source.Id, AssetKind.Tie);
        var correct = workspace.ResolveAsset(reference, catalog)!;
        var native = UyaCanonicalAssetCodec.Decode(await File.ReadAllBytesAsync(correct.Path));
        var original = CatalogModelCodec.Decode(await workspace.ReadAttachedAssetVerifiedAsync(reference,
            ForgeProjectWorkspace.MaxAttachedAssetBytes, default));
        var legacy = original.ModelBytes.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(legacy.AsSpan(0x34),
            BinaryPrimitives.ReadUInt32LittleEndian(legacy.AsSpan(0x34)) + 16);
        var classId = workspace.Content.CustomClasses.Allocations.Single(value => value.Asset == reference).ClassId;
        BinaryPrimitives.WriteUInt16LittleEndian(legacy.AsSpan(0x44), checked((ushort)classId));
        var attachments = new ProjectAssetAttachmentCollector();
        var previous = attachments.Attach(AssetKind.Tie, 1,
            UyaCanonicalAssetCodec.Encode(native.DefinitionBytes, legacy, native.Textures), source.Id);
        await workspace.CommitAssetsAsync(workspace.Content with
        {
            AssetOverrides = workspace.Content.AssetOverrides.Select(value => value.Source == reference
                ? value with { Replacement = previous } : value).ToArray(),
        }, attachments.Attachments);
        await workspace.SaveAsync();
        var before = workspace.OcclusionVisibilityFingerprint;
        var entities = workspace.Content.Entities.ToArray();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await UyaConvertedAssetRepairService.HydrateAsync(workspace, cancelled.Token); throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { }
        Check(workspace.OcclusionVisibilityFingerprint == before, "cancelled repair preserves project state");
        var diagnostic = await UyaProjectOpenHydrationService.HydrateAsync(
            workspace, catalog, new(new("tests.empty", 1, [])), default);
        Check(diagnostic?.Message.Contains("Repaired model/texture conversion", StringComparison.Ordinal) == true,
            "opening a project repairs previous DL tie conversions");
        Check(workspace.ResolveAssetReference(reference) == correct.Effective, "repair reuses the corrected native attachment");
        Check(workspace.Content.Entities.SequenceEqual(entities), "repair preserves instances and assigned classes");
        Check(workspace.OcclusionVisibilityFingerprint != before, "repair invalidates stale occlusion and build inputs");
        Check(File.Exists(workspace.ResolveAttachedAssetPath(previous.Id)), "repair retains the previous model blob");
        await workspace.SaveAsync();
        var reopened = await ForgeProjectWorkspace.OpenAsync(workspace.RootPath);
        Check(await UyaConvertedAssetRepairService.HydrateAsync(reopened, default) is null, "repair is stable after save and reopen");
    }

    public static async Task QualifyAsync(string sourceCatalogPath, string sourceIsoPath, string projectPath)
    {
        var original = await AssetCatalogStore.OpenReadOnlyAsync(sourceCatalogPath);
        var catalog = await AssetCatalogStore.OpenAsync(Path.Combine(projectPath, "qualification-catalog"));
        var workspace = await ForgeProjectWorkspace.CreateAsync(projectPath, "DL placement qualification",
            new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"), new("UYA", "NTSC-U", "1.00", 3, new string('a', 32)), []);
        await UyaStaticLayerStore.WriteSourceAsync(projectPath, new("UYA", "NTSC-U", "1.00", 3, new string('a', 32)),
        [
            new(BakeLayerId.Ties, UyaTieInstancesReader.RecordSize, [0, 0, 0], []),
            new(BakeLayerId.Shrubs, UyaShrubInstancesReader.RecordSize, [0, 0, 0], []),
            new(BakeLayerId.Mobys, UyaMobyInstancesReader.RecordSize, [0, 0, 0], []),
        ]);
        using var iso = File.OpenRead(sourceIsoPath);
        var sourceWad = LevelArchiveReader.ExtractPrimary(GameId.UYA, iso, 3);
        var source = LevelArchiveReader.ReadAssetSourceFiles(GameId.UYA, sourceWad);
        var sourceBank = UyaLevelWadReader.ReadSectorFileBlock(sourceWad, UyaLevelWadReader.ReadLevelWad(sourceWad).SoundBank);
        var soundInventory = SoundBankReader.Inspect(GameId.UYA, sourceBank);
        await workspace.SetSoundsAsync(SoundProjectService.Create(GameId.UYA, 3, sourceBank, soundInventory,
            SoundBankReader.InspectRemap(GameId.UYA, source.HeaderBytes, soundInventory.Programs.Count), new Dictionary<int, AssetCatalogEntry>())
            with { MobyRemaps = [] });
        var selected = new List<AssetCatalogEntry>();
        foreach (var kind in new[] { AssetKind.Tie, AssetKind.Shrub, AssetKind.Moby })
        {
            var entry = original.Query(new(Kind: kind, Game: "DL", Limit: 1000)).First(value => kind != AssetKind.Moby
                || CatalogModelCodec.Decode(File.ReadAllBytes(original.ResolveBlobPath(value.Id)!)).ModelBytes[8] > 0
                    && value.Sources.Any(source => source.MobySounds is { Slots.Count: > 0 } sounds && sounds.Slots.All(slot => slot.Sound is not null)));
            var bytes = await File.ReadAllBytesAsync(original.ResolveBlobPath(entry.Id)!);
            var chosenSource = entry.Sources.First(value => value.Game == "DL" && (kind != AssetKind.Moby
                || value.MobySounds is { Slots.Count: > 0 } sounds && sounds.Slots.All(slot => slot.Sound is not null)));
            if (chosenSource.MobySounds is { } use)
            {
                var bank = original.Query(new(Id: use.Bank)).Single();
                await catalog.PutAsync(AssetKind.SoundBank, bank.CanonicalFormatVersion,
                    await File.ReadAllBytesAsync(original.ResolveBlobPath(bank.Id)!),
                    new(bank.ImporterVersion, bank.Sources[0], bank.Aliases, bank.Tags));
                var slot = use.Slots.First(value => value.PreviewSupported);
                var audio = await SoundWorkspaceService.RenderCatalogPreviewAsync(catalog.RootPath, bank.Id, "DL", slot.NativeProgramIndex, true, default);
                Check(audio.Pcm16.Length > 0, "retail DL moby sound catalog playback");
            }
            entry = await catalog.PutAsync(kind, entry.CanonicalFormatVersion, bytes,
                new(entry.ImporterVersion, chosenSource, entry.Aliases, entry.Tags));
            selected.Add(entry);
            var entity = await UyaAssetPlacementService.CreateAsync(workspace, catalog.RootPath,
                new(entry.Id, kind, AssetCatalogPaging.ClassIds(entry)[0], ProjectTransform.Identity), default);
            workspace.AddEntity(entity);
            var preview = await UyaAssetPreviewService.PrepareAsync(new(
                Path.Combine(projectPath, "preview-cache"), catalog.RootPath, entry.Id, kind, "UYA", "model-default",
                ProjectPath: projectPath), "qualification", projectWorkspace: workspace);
            Check(File.Exists(Path.Combine(preview.RootPath, preview.ModelPath)), "converted project preview");
        }
        await workspace.SaveAsync();
        workspace = await ForgeProjectWorkspace.OpenAsync(projectPath);
        var inputs = await UyaStaticLayerStore.CreateBakeInputsAsync(workspace, catalog, default);
        Check(inputs.All(input => input.Blockers?.Count is null or 0), "real converted layer blockers");
        var plan = BakeLayerGraph.CreatePlan(new(workspace.Manifest.Target, "qualification", "qualification"), inputs);
        var staging = await BakeStagingStore.OpenAsync(projectPath);
        foreach (var layer in UyaStaticLayerSchema.Layers)
            await UyaStaticLayerStore.StageAsync(workspace, catalog, staging, plan.Layers.Single(value => value.Layer == layer), default);
        _ = await UyaTextureInventoryService.BuildAsync(workspace, catalog, default);
        var converted = new List<StaticAssetInput>();
        foreach (var entry in selected)
        {
            var resolved = workspace.ResolveAsset(new(entry.Id, entry.Kind), catalog)!;
            var native = UyaCanonicalAssetCodec.Decode(await File.ReadAllBytesAsync(resolved.Path));
            converted.Add(new(entry.Id.ToString(), entry.Kind switch
            {
                AssetKind.Tie => TextureAssetFamily.Tie, AssetKind.Shrub => TextureAssetFamily.Shrub, _ => TextureAssetFamily.Moby,
            }, workspace.Content.CustomClasses.Allocations.Single(value => value.Asset.Id == entry.Id).ClassId,
                native.DefinitionBytes, native.ModelBytes,
                native.Textures.Select(texture => new StaticAssetTexture(texture.Role == 0 ? TextureRole.Material : TextureRole.Billboard, texture.PifBytes)).ToArray()));
        }
        var assetWad = BinaryMagic.IsWad(source.AssetWadBytes)
            ? WadCompression.Decompress(source.AssetWadBytes) : source.AssetWadBytes;
        var composed = StaticAssetComposer.Compose(GameId.UYA, source.HeaderBytes, assetWad, source.PaletteBytes, converted);
        var header = LevelAssetReader.ReadHeader(composed.HeaderBytes);
        Check(header.TieModelCount == 1 && header.ShrubModelCount == 1 && header.MobyModelCount == 1,
            "converted models composed into UYA native asset tables");
        var soundComposition = await UyaSoundCompositionService.ComposeAsync(
            workspace, sourceBank, source.HeaderBytes, composed.HeaderBytes, default);
        var outputInventory = SoundBankReader.Inspect(GameId.UYA, soundComposition.BankBytes);
        var finalHeader = SoundBankComposer.ComposeRemap(GameId.UYA, composed.HeaderBytes, soundComposition.Remap);
        var outputRemap = SoundBankReader.InspectRemap(GameId.UYA, finalHeader, outputInventory.Programs.Count);
        var mobyClass = workspace.Content.CustomClasses.Allocations.Single(value => value.Asset.Kind == AssetKind.Moby).ClassId;
        var map = outputRemap.Mobys.Single(value => value.ClassId == mobyClass);
        var expectedMap = workspace.Content.Sounds.MobyRemaps.Single(value => value.ClassId == mobyClass);
        Check(map.Slots.Count > 0 && map.Slots.Zip(expectedMap.Slots).All(pair => pair.First.NativeProgramIndex < 0
            || ProjectSounds.ProgramId(outputInventory.Programs[pair.First.NativeProgramIndex].ContentId!) == pair.Second.Sound!.Id),
            "DL sounds re-read from the baked UYA bank and remap with identical identities");
        await UyaDlAssetResourceTests.QualifySkiesAsync(original, catalog, workspace, source.HeaderBytes, assetWad);
        Console.WriteLine("Retail DL models: placement, native preview, save/reopen, static bake, texture inventory, UYA asset/sound-bank composition and all DL sky conversions passed.");
    }

    public static async Task QualifyRepairAsync(string sourceProjectPath, string scratchPath)
    {
        if (Directory.Exists(scratchPath)) throw new InvalidOperationException("Qualification requires a new scratch project directory.");
        Directory.CreateDirectory(scratchPath);
        File.Copy(Path.Combine(sourceProjectPath, "forge-project.json"), Path.Combine(scratchPath, "forge-project.json"));
        foreach (var directory in new[] { "assets", "content" })
        foreach (var path in Directory.EnumerateFiles(Path.Combine(sourceProjectPath, directory), "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(scratchPath, Path.GetRelativePath(sourceProjectPath, path));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(path, destination);
        }
        var workspace = await ForgeProjectWorkspace.OpenAsync(scratchPath);
        var before = ForgeProjectPersistence.Serialize(workspace.Content.Entities);
        Check(await UyaConvertedAssetRepairService.HydrateAsync(workspace, default) is not null, "real project has legacy conversions to repair");
        Check(before.SequenceEqual(ForgeProjectPersistence.Serialize(workspace.Content.Entities)), "real project repair retains every entity");
        var count = 0;
        foreach (var allocation in workspace.Content.CustomClasses.Allocations.Where(value =>
            value.Asset.Kind == AssetKind.Tie && value.Provenance.Origin == ProjectCustomClassOrigin.ConvertedAsset))
        {
            var source = CatalogModelCodec.Decode(await workspace.ReadAttachedAssetVerifiedAsync(
                allocation.Asset, ForgeProjectWorkspace.MaxAttachedAssetBytes, default));
            var native = UyaCanonicalAssetCodec.Decode(await workspace.ReadAttachedAssetVerifiedAsync(
                workspace.ResolveAssetReference(allocation.Asset), ForgeProjectWorkspace.MaxAttachedAssetBytes, default));
            var dl = TieClassReader.Read(source.ModelBytes, TieClassReadOptions.ForGameProfile(TieGameProfile.ForGame(GameId.DL)));
            var uya = TieClassReader.Read(native.ModelBytes, TieClassReadOptions.ForGameProfile(TieGameProfile.ForGame(GameId.UYA)));
            Check(dl.Header.VertexNormalsOffset == uya.Header.VertexNormalsOffset
                && dl.Header.RgbaRemapOffsets.SequenceEqual(uya.Header.RgbaRemapOffsets)
                && dl.Header.UnknownOffsets78.SequenceEqual(uya.Header.UnknownOffsets78), "real project preserves all remap addresses");
            var firstRemap = checked((int)dl.Header.VertexNormalsOffset + dl.Header.RgbaRemapOffsets.Where(value => value > 0).Min());
            Check(source.ModelBytes.AsSpan(firstRemap).SequenceEqual(native.ModelBytes.AsSpan(firstRemap)),
                "real project preserves remap, shader and trailing model bytes exactly");
            for (var index = 0; index < source.Textures.Count; index++)
            {
                var originalTexture = PifReader.Read(source.Textures[index].PifBytes);
                var targetTexture = PifReader.Read(native.Textures[index].PifBytes);
                if (targetTexture.MipPixelData.Count > 1)
                    Check(originalTexture.MipPixelData[1].SequenceEqual(targetTexture.MipPixelData[1]),
                        "real project preserves the smallest linear GS-bank mip byte for byte");
            }
            count++;
        }
        await workspace.SaveAsync();
        workspace = await ForgeProjectWorkspace.OpenAsync(scratchPath);
        Check(await UyaConvertedAssetRepairService.HydrateAsync(workspace, default) is null, "real project repair persists across reopening");
        Console.WriteLine($"Repaired {count} converted DL TIE(s) in an isolated project copy; entity records, remap/shader bytes and smallest linear mips preserved; save/reopen passed.");
    }

    private static EditorCommand Place(AssetCatalogEntry entry) => new(Guid.NewGuid().ToString("D"),
        EditorCommandKind.CreateEntityFromAsset, [], Placement: new(entry.Id, entry.Kind, 0x1234, ProjectTransform.Identity));

    private static AssetImportMetadata Metadata(AssetKind kind) => new("conversion-test",
        new("DL", "NTSC-U", "1.00", "level01", "assets/asset_wad.bin", 0, new string('b', 32)),
        [$"{kind.ToString().ToLowerInvariant()}:0x1234"], ["vanilla", "game:DL"]);

    private static async Task<AssetExplorerPagePayload> QueryAsync(string root, AssetCatalogEntry entry)
    {
        var category = entry.Kind switch
        {
            AssetKind.Tie => AssetExplorerCategoryPayload.Ties,
            AssetKind.Shrub => AssetExplorerCategoryPayload.Shrubs,
            _ => AssetExplorerCategoryPayload.Mobys,
        };
        var request = new AssetExplorerRequestPayload(root, category, entry.Id.ToString(), null, null, null, null,
            [], null, 64, "UYA", "NTSC-U", "1.00", 0);
        return AssetExplorerPayloadCodec.DecodePage(await ProjectBridgeHandlers.HandleAsync(
            new(BridgeMessageKind.Request, BridgeOpcode.QueryAssetExplorer, BridgeErrorCode.None, 1,
                AssetExplorerPayloadCodec.EncodeRequest(request)), "test-sdk", _ => ValueTask.CompletedTask, default));
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
