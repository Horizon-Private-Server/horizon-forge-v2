using System.Buffers.Binary;
using Forge.Host.Domain;
using Forge.Host.Games.UYA;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.Textures.Pif;
using RatchetPs2.Sdk;

namespace Forge.Host.ProtocolTests.Games.UYA;

internal static class UyaFxContentImportTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-fx-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var catalog = await AssetCatalogStore.OpenAsync(Path.Combine(root, "catalog"));
            var targetFingerprint = new string('a', 32);
            var donorFingerprint = new string('b', 32);
            var donorBytes = Texture(8, 4, 0x73);
            var donorAsset = await catalog.PutAsync(
                AssetKind.Texture,
                ProjectTextureAssetSchema.CanonicalFormatVersion,
                donorBytes,
                new(
                    "donor-fx",
                    new("UYA", "NTSC-U", "1.00", "level40", "level_wad/assets/asset_wad.bin", 0,
                        donorFingerprint),
                    ["fx:0"],
                    ["vanilla", "game:UYA", "level:40", "fx"]));
            var donor = new ProjectFxState(
                ProjectFxSchema.CurrentVersion,
                [new(0, "FX_LAME_SHADOW", 8, 4, 0, 0x400, false,
                    new(donorAsset.Id, AssetKind.Texture))],
                []);
            var target = new ProjectFxState(
                ProjectFxSchema.CurrentVersion,
                [
                    Source(0, 4, 4, 0x11),
                    Source(1, 4, 4, 0x22),
                ],
                []);

            var projectPath = Path.Combine(root, "project");
            var workspace = await ForgeProjectWorkspace.CreateAsync(
                projectPath,
                "FX import",
                new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
                new("UYA", "NTSC-U", "1.00", 41, targetFingerprint),
                [],
                null,
                null,
                target);
            var family = new ContentImportFamilyPlan(
                "fx", "FX", ContentImportMode.Replace,
                ContentImportMode.Replace, ContentImportSourceState.Present,
                true, false, true,
                ["source:fx:singleton"], [], [], ["fx:project"], [],
                ["fx requires the complete ordered texture bank."], [],
                donorBytes.Length, [], []);
            var plan = new ContentImportPlan(
                ContentImportPlanSchema.CurrentVersion,
                ContentImportCapabilitySchema.CurrentVersion,
                $"import-{new string('c', 32)}",
                "UYA",
                "NTSC-U",
                "1.00",
                donorFingerprint,
                new string('d', 64),
                40,
                workspace.Manifest.ProjectId.ToString(),
                workspace.CurrentFingerprint,
                41,
                donorBytes.Length,
                [family],
                [],
                []);
            var command = await UyaFxContentImportService.PrepareCommitAsync(
                workspace, catalog, donor, plan, 1234);
            Equal(1, command.Attachments.Count, "donor FX texture is attached");
            Equal(true, command.Batch.Replaced.SequenceEqual(["fx:project"]),
                "target FX bank is recorded as replaced");

            await workspace.CommitContentImportAsync(command);
            await workspace.SaveAsync();
            workspace = await ForgeProjectWorkspace.OpenAsync(projectPath);
            Equal(true, workspace.Content.Fx!.ReplaceSourceBank,
                "imported FX state requests whole-bank replacement");
            Equal(donorAsset.Id, workspace.Content.Fx.SourceTextures.Single().Texture!.Id,
                "donor FX texture identity survives reopen");
            var repeatPlan = plan with
            {
                BatchId = $"import-{new string('e', 32)}",
                TargetFingerprint = workspace.CurrentFingerprint,
            };
            var repeat = await UyaFxContentImportService.PrepareCommitAsync(
                workspace, catalog, donor, repeatPlan, 2345);
            Equal(0, repeat.Attachments.Count,
                "reimport does not restage project-owned FX textures");
            Equal(true, repeat.Batch.Reused.SequenceEqual([$"asset:{donorAsset.Id}"]),
                "reimport records the FX texture as reused");
            var input = await UyaFxBakeService.CreateBakeInputAsync(workspace, CancellationToken.None);
            Equal(true, input.AssetIds.Contains(donorAsset.Id),
                "FX bake tracks the portable donor texture");

            var staging = await BakeStagingStore.OpenAsync(projectPath);
            var snapshot = await UyaFxBakeService.StageAsync(
                projectPath,
                staging,
                new(BakeLayerId.Fx, BakeLayerState.Dirty, new string('e', 64), new string('f', 64), [], []),
                CancellationToken.None);
            var fixture = TargetAssetFixture();
            var composition = await UyaFxBakeService.ComposeAsync(
                Path.Combine(staging.RootPath, snapshot.RelativePath),
                fixture.Header,
                fixture.Asset,
                CancellationToken.None)
                ?? throw new InvalidOperationException("Expected a staged whole-bank FX replacement.");
            var actual = FxTextureCatalog.Read(GameId.UYA, composition.HeaderBytes, composition.AssetBytes);
            Equal(1, actual.Entries.Count, "whole-bank import shrinks the target inventory to donor count");
            Equal(true, actual.Entries[0].CanonicalTextureBytes.SequenceEqual(donorBytes),
                "composed FX bank preserves the donor texture exactly");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ProjectFxSourceTexture Source(int index, int width, int height, byte marker)
    {
        var bytes = Texture(width, height, marker);
        return new(
            index,
            $"FX_TEXTURE_{index}",
            width,
            height,
            index * 0x500,
            index * 0x500 + 0x400,
            false,
            new(AssetId.Compute(AssetKind.Texture, ProjectTextureAssetSchema.CanonicalFormatVersion, bytes),
                AssetKind.Texture));
    }

    private static (byte[] Header, byte[] Asset) TargetAssetFixture()
    {
        var header = new byte[0x100];
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x18), 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x1c), 0xe0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x58), 2);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x5c), 0xc0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x68), 0x100);
        WriteDefinition(header.AsSpan(0xc0, 0x10), 0, 0x400, 4, 4);
        WriteDefinition(header.AsSpan(0xd0, 0x10), 0x500, 0x900, 4, 4);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0xe0), 0xb00);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0xe4), 1);
        var asset = Enumerable.Range(0, 0xb20).Select(index => unchecked((byte)index)).ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x7c), asset.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x8c), asset.Length);
        return (header, asset);
    }

    private static byte[] Texture(int width, int height, byte marker)
    {
        var palette = Enumerable.Range(0, 0x400).Select(index => unchecked((byte)(marker + index))).ToArray();
        var pixels = Enumerable.Range(0, width * height).Select(index => unchecked((byte)(marker ^ index))).ToArray();
        return PifWriter.Write(PifWriter.CreateIndexed8(width, height, palette, pixels));
    }

    private static void WriteDefinition(
        Span<byte> destination,
        int paletteOffset,
        int pixelOffset,
        int width,
        int height)
    {
        BinaryPrimitives.WriteInt32LittleEndian(destination, paletteOffset);
        BinaryPrimitives.WriteInt32LittleEndian(destination[4..], pixelOffset);
        BinaryPrimitives.WriteInt32LittleEndian(destination[8..], width);
        BinaryPrimitives.WriteInt32LittleEndian(destination[12..], height);
    }

    private static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected {expected}, got {actual}");
    }
}
