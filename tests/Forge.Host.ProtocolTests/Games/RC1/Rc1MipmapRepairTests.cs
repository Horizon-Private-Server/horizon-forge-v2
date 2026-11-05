using Forge.Host.Domain;
using Forge.Host.Games.RC1;
using Forge.Host.Games.UYA;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.Textures.Pif;
using RatchetPs2.Games.UYA.Gameplay;

namespace Forge.Host.ProtocolTests.Games.RC1;

internal static class Rc1MipmapRepairTests
{
    public static async Task RunAsync()
    {
        await VerifyAsync(sharedBlob: false);
        await VerifyAsync(sharedBlob: true);
    }

    private static async Task VerifyAsync(bool sharedBlob)
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-rc1-mipmaps-{Guid.NewGuid():N}");
        try
        {
            var catalogPath = Path.Combine(root, "catalog");
            await Rc1AssetImportService.ImportValidatedAsync(new MemoryStream(Rc1AssetImportTests.CreateIso()),
                new(GameId.RC1, "synthetic.iso", catalogPath, new string('a', 32), "1.00", "rc1-test"));
            var catalog = await AssetCatalogStore.OpenAsync(catalogPath);
            var template = catalog.Query(new(Kind: AssetKind.Tie)).Single();
            var model = CatalogModelCodec.Decode(await File.ReadAllBytesAsync(catalog.ResolveBlobPath(template.Id)!));
            var palette = new byte[1024];
            for (var index = 0; index < 256; index++) palette[index * 4 + 3] = 128;
            var pixels = Enumerable.Repeat((byte)1, 32 * 32).ToArray();
            var oldPif = PifWriter.Write(PifWriter.CreateIndexed8(32, 32, palette, pixels));
            var correctedPif = PifWriter.Write(PifWriter.CreateIndexed8(32, 32, palette, pixels,
                [Enumerable.Repeat((byte)2, 16 * 16).ToArray(), Enumerable.Repeat((byte)3, 8 * 8).ToArray(),
                    Enumerable.Repeat((byte)4, 4 * 4).ToArray()]));
            var original = await catalog.PutAsync(AssetKind.Tie, 1,
                CatalogModelCodec.Encode(GameId.RC1, model.DefinitionBytes, model.ModelBytes, [model.Textures[0] with { PifBytes = oldPif }]),
                new("forge-rc1-v1+test", template.Sources[0], template.Aliases, template.Tags));
            var variantModel = CatalogModelCodec.Decode(await File.ReadAllBytesAsync(catalog.ResolveBlobPath(original.Id)!));
            variantModel.ModelBytes[0x6c] ^= 1; // This unused source field does not change the native conversion.
            var variant = await catalog.PutAsync(AssetKind.Tie, 1,
                CatalogModelCodec.Encode(GameId.RC1, variantModel.DefinitionBytes, variantModel.ModelBytes, variantModel.Textures),
                new("forge-rc1-v1+test", template.Sources[0], template.Aliases, template.Tags));
            var project = Path.Combine(root, "project");
            await ForgeProjectWorkspace.CreateAsync(project, "RC1 mipmaps", new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
                new("UYA", "NTSC-U", "1.00", 3, new string('b', 32)), []);
            await UyaStaticLayerStore.WriteSourceAsync(project, new("UYA", "NTSC-U", "1.00", 3, new string('b', 32)),
                [new(BakeLayerId.Ties, UyaTieInstancesReader.RecordSize, [0, 0, 0], []),
                    new(BakeLayerId.Shrubs, UyaShrubInstancesReader.RecordSize, [0, 0, 0], []),
                    new(BakeLayerId.Mobys, UyaMobyInstancesReader.RecordSize, [0, 0, 0], [])]);
            await using (var runtime = new EditorRuntime(placementResolver: UyaAssetPlacementService.CreateAsync))
            {
                await runtime.OpenAsync(project, catalogPath, TimeSpan.Zero);
                if (sharedBlob)
                {
                    var first = await runtime.ExecuteAsync(new(Guid.NewGuid().ToString("D"), EditorCommandKind.CreateEntityFromAsset, [],
                        Placement: new(variant.Id, AssetKind.Tie, 0x1234, ProjectTransform.Identity, AssetSourceSelection.From(variant.Sources[0]))));
                    await runtime.ExecuteAsync(new(Guid.NewGuid().ToString("D"), EditorCommandKind.DeleteEntities, first.Selection));
                }
                await runtime.ExecuteAsync(new(Guid.NewGuid().ToString("D"), EditorCommandKind.CreateEntityFromAsset, [],
                    Placement: new(original.Id, AssetKind.Tie, 0x1234, ProjectTransform.Identity, AssetSourceSelection.From(original.Sources[0]))));
                await runtime.SaveAsync();
            }
            var workspace = await ForgeProjectWorkspace.OpenAsync(project);
            var binding = workspace.Content.AssetOverrides.Single(value => value.Source.Id == original.Id);
            Check(workspace.Content.Assets.Single(value => value.Id == binding.Replacement.Id).ParentId == (sharedBlob ? variant.Id : original.Id),
                "variant swaps reuse the native blob with its original parent");
            var native = UyaCanonicalAssetCodec.Decode(await workspace.ReadAttachedAssetVerifiedAsync(
                binding.Replacement, ForgeProjectWorkspace.MaxAttachedAssetBytes, default));
            Check(PifReader.Read(native.Textures[0].PifBytes).Header.MipLevels == 1, "fixture starts with missing mipmaps");
            native.ModelBytes[0x4c] ^= 1; // An independent edit must survive texture repair.
            var edited = new ProjectAssetAttachmentCollector();
            var parent = sharedBlob ? variant.Id : original.Id;
            var replacement = edited.Attach(AssetKind.Tie, 1, UyaCanonicalAssetCodec.Encode(native.DefinitionBytes, native.ModelBytes, native.Textures), parent);
            await workspace.CommitAssetsAsync(workspace.Content with { AssetOverrides = workspace.Content.AssetOverrides.Select(value =>
                value.Source.Id == original.Id ? binding with { Replacement = replacement } : value).ToArray() }, edited.Attachments, default);
            var entities = workspace.Content.Entities.ToArray();
            var classes = workspace.Content.CustomClasses;
            var correctedBytes = CatalogModelCodec.Encode(GameId.RC1, model.DefinitionBytes, model.ModelBytes,
                [model.Textures[0] with { PifBytes = correctedPif }]);
            await catalog.PutAsync(AssetKind.Tie, 1, correctedBytes,
                new(Rc1AssetImportService.ImporterVersionPrefix + "test", template.Sources[0] with { Fingerprint = new string('f', 32) }, template.Aliases, template.Tags));
            var before = workspace.CurrentFingerprint;
            await UyaConvertedRc1TextureRepairService.HydrateAsync(workspace, catalog, default);
            Check(workspace.CurrentFingerprint == before, "a different source fingerprint cannot repair a placement");
            await catalog.PutAsync(AssetKind.Tie, 1, correctedBytes,
                new(Rc1AssetImportService.ImporterVersionPrefix + "test", template.Sources[0], template.Aliases, template.Tags));
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                try { await UyaConvertedRc1TextureRepairService.HydrateAsync(workspace, catalog, cancelled.Token); throw new Exception("Expected cancellation"); }
                catch (OperationCanceledException) { }
                Check(workspace.CurrentFingerprint == before, "cancelled repair preserves project state");
            }
            Check(await UyaConvertedRc1TextureRepairService.HydrateAsync(workspace, catalog, default) is not null, "existing placement is repaired");
            var repaired = UyaCanonicalAssetCodec.Decode(await workspace.ReadAttachedAssetVerifiedAsync(
                workspace.Content.AssetOverrides.Single(value => value.Source.Id == original.Id).Replacement, ForgeProjectWorkspace.MaxAttachedAssetBytes, default));
            Check(repaired.ModelBytes.AsSpan().SequenceEqual(native.ModelBytes), "model edits survive mip repair");
            var texture = PifReader.Read(repaired.Textures[0].PifBytes);
            Check(texture.Header.MipLevels == 3 && texture.PixelData.SequenceEqual(pixels)
                && texture.MipPixelData[0].All(value => value == 2) && texture.MipPixelData[1].All(value => value == 3), "UYA keeps the authored base, half and quarter images");
            Check(workspace.Content.Entities.SequenceEqual(entities) && workspace.Content.CustomClasses == classes, "placement identity and class allocation are retained");
            before = workspace.CurrentFingerprint;
            await UyaConvertedRc1TextureRepairService.HydrateAsync(workspace, catalog, default);
            Check(workspace.CurrentFingerprint == before, "repair is idempotent");
            await workspace.SaveAsync();
            Check((await ForgeProjectWorkspace.OpenAsync(project)).CurrentFingerprint == before, "repaired textures survive save/reopen");

            // A replacement texture is user content, even if it also lacks mipmaps.
            var customPif = PifWriter.Write(PifWriter.CreateIndexed8(32, 32, palette, Enumerable.Repeat((byte)9, 1024).ToArray()));
            var custom = new ProjectAssetAttachmentCollector();
            replacement = custom.Attach(AssetKind.Tie, 1, UyaCanonicalAssetCodec.Encode(native.DefinitionBytes, native.ModelBytes,
                [native.Textures[0] with { PifBytes = customPif }]), parent);
            await workspace.CommitAssetsAsync(workspace.Content with { AssetOverrides = [binding with { Replacement = replacement }] }, custom.Attachments, default);
            before = workspace.CurrentFingerprint;
            await UyaConvertedRc1TextureRepairService.HydrateAsync(workspace, catalog, default);
            Check(workspace.CurrentFingerprint == before, "independent texture edits are preserved");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
