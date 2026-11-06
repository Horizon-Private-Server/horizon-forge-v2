using Forge.Host.Domain;
using Forge.Host.Games.UYA;
using RatchetPs2.Core.Games;
using RatchetPs2.Sdk;
using System.Text.Json;
using System.Buffers.Binary;
using RatchetPs2.Core.IO;
using RatchetPs2.Core.Wad;

namespace Forge.Host.ProtocolTests.Games.UYA;

internal static class UyaOcclusionCoverageBuildTests
{
    internal static async Task VerifyAsync(string project, AssetCatalogStore catalog,
        BakeFingerprintContext context, byte[] sourceWad, string? expectedHash)
    {
        var workspace = await ForgeProjectWorkspace.OpenAsync(project);
        var original = workspace.Content.OcclusionCoverage;
        var stageBefore = ForgeProjectPersistence.Serialize((await BakeStagingStore.OpenAsync(project)).Manifest);
        // The synthetic source omits a scene-view boundary, which native grid insertion requires.
        var sourceAssets = LevelArchiveReader.ReadAssetSourceFiles(GameId.UYA, sourceWad);
        var assetLength = BinaryMagic.IsWad(sourceAssets.AssetWadBytes)
            ? WadCompression.Decompress(sourceAssets.AssetWadBytes).Length : sourceAssets.AssetWadBytes.Length;
        BinaryPrimitives.WriteInt32LittleEndian(sourceAssets.HeaderBytes.AsSpan(0x7c), assetLength);
        var sourceWithBoundary = LevelArchiveBuilder.Build(GameId.UYA, sourceWad,
            new Dictionary<string, ReadOnlyMemory<byte>> { ["assets/asset_header.bin"] = sourceAssets.HeaderBytes }).OutputBytes!;
        workspace.SetPrimaryOcclusionCoverageEnabled(false);
        await workspace.SaveAsync();
        var bake = await UyaBakeService.BakeAsync(project, catalog, context,
            includedLayers: new HashSet<BakeLayerId> { BakeLayerId.Ties });
        Check(bake.Succeeded && bake.WrittenLayers.Count == 0,
            "Disabling all coverage must permit selective baking without generating visibility.");
        var packed = await UyaLevelPackService.PackAsync(project, catalog, sourceWithBoundary, context);
        Check(packed.Succeeded && OcclusionComposer.ReadGrid(GameId.UYA, packed.OutputBytes!)
            .SequenceEqual(OcclusionComposer.CreateEmptyGrid(GameId.UYA)),
            "Disabled occlusion packs an empty native template instead of retaining Primary: "
            + string.Join(" | ", packed.Diagnostics.Select(d => d.Code + ": " + d.Cause)));
        var cachePath = Path.Combine(project, "cache", "occlusion", "generated.json");
        var previousCache = File.Exists(cachePath) ? await File.ReadAllBytesAsync(cachePath) : null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            await File.WriteAllTextAsync(cachePath, "malformed stale visibility");
            var withoutCache = await UyaLevelPackService.PackAsync(project, catalog, sourceWithBoundary, context);
            Check(withoutCache.Succeeded && withoutCache.OutputSha256 == packed.OutputSha256,
                "Disabled occlusion must ignore old visibility bakes, including corrupt caches.");
            var visibilityRoot = Directory.CreateDirectory(Path.Combine(project, "empty-visibility")).FullName;
            await UyaOcclusionVisibilityService.ExportAsync(project, "no-source-iso-required", visibilityRoot, default);
            using var visibility = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(visibilityRoot, "visibility.json")));
            Check(visibility.RootElement.GetProperty("octants").GetArrayLength() == 0
                && visibility.RootElement.GetProperty("masks").GetString() == "",
                "Disabled viewport visibility is empty without reading source masks or caches.");
        }
        finally
        {
            if (previousCache is null) File.Delete(cachePath);
            else await File.WriteAllBytesAsync(cachePath, previousCache);
        }

        var box = new ProjectOcclusionCoverageSource(EntityId.New(), "Custom coverage", true,
            ProjectOcclusionCoverageRole.Include, ProjectTransform.Identity, BoxSize: new(4, 4, 4));
        workspace.SetOcclusionCoverage(original with { Sources = [box] });
        await workspace.SaveAsync();
        packed = await UyaLevelPackService.PackAsync(project, catalog, sourceWad, context);
        Check(!packed.Succeeded && packed.Diagnostics.Any(d => d.Code == "UYA_OCCLUSION_COVERAGE_NOT_GENERATED"),
            "An enabled authored volume must block source-only packing even when Primary stays enabled.");

        workspace.SetOcclusionCoverage(new(1, false, [box with { Enabled = false }]));
        await workspace.SaveAsync();
        packed = await UyaLevelPackService.PackAsync(project, catalog, sourceWithBoundary, context);
        Check(packed.Succeeded && OcclusionComposer.ReadGrid(GameId.UYA, packed.OutputBytes!)
            .SequenceEqual(OcclusionComposer.CreateEmptyGrid(GameId.UYA)), "Disabled custom helpers must also pack the empty template.");

        workspace.SetOcclusionCoverage(original with { Sources = [box with { Enabled = false }] });
        await workspace.SaveAsync();
        packed = await UyaLevelPackService.PackAsync(project, catalog, sourceWad, context);
        Check(packed.Succeeded && packed.OutputSha256 == expectedHash,
            "Disabled authored helpers must preserve the original source-mode output.");
        workspace.SetOcclusionCoverage(original);
        await workspace.SaveAsync();
        var stageAfter = ForgeProjectPersistence.Serialize((await BakeStagingStore.OpenAsync(project)).Manifest);
        Check(stageBefore.SequenceEqual(stageAfter), "Coverage mode changes must preserve the current layer stage manifest.");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
