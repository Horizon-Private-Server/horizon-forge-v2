using Forge.Host.Domain;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.LevelAssets;
using RatchetPs2.Games.UYA.Level;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.UYA;

internal sealed record UyaTfragPackResult(
    byte[] TerrainBytes,
    byte[]? OcclusionBytes,
    int? VisibilityBit);

internal static class UyaTfragPackService
{
    public static async Task<UyaTfragPackResult> ComposeAsync(
        ForgeProjectWorkspace workspace,
        UyaLevelWadPackage sourcePackage,
        string stagedTieRoot,
        byte[] terrainBytes,
        byte[] sourceTerrainBytes,
        IReadOnlyDictionary<int, IReadOnlyDictionary<int, string>> textureAssetIdsByGroup,
        IReadOnlyList<TerrainTextureAllocation> terrainTextures,
        int? visibilityBit,
        CancellationToken cancellationToken)
    {
        var state = workspace.Content.Tfrags;
        if (terrainBytes.Length < 0x40 || state is null)
            return new(terrainBytes, null, visibilityBit);

        if (UyaBaseLayerService.ReadPrimaryTerrain(sourceTerrainBytes) is null
            && state.CustomImports.Count == 0)
            return new(terrainBytes, null, visibilityBit);

        var retainedNativeCount = workspace.Content.Entities.Count(entity => entity.Tfrag is
        {
            SourceKind: ProjectTfragSourceKind.Primary,
            SourceIndex: 0,
            CustomImportId: null,
        });
        var stagedOcclusionPath = Path.Combine(stagedTieRoot, "occlusion.bin");
        var occlusionBytes = File.Exists(stagedOcclusionPath)
            ? await File.ReadAllBytesAsync(stagedOcclusionPath, cancellationToken)
            : sourcePackage.Files.Single(value => value.Path == "gameplay/core/occlusion.bin").Bytes;
        var occlusion = TfragTerrainComposer.ComposeOcclusionMappings(
            GameId.UYA,
            terrainBytes,
            sourceTerrainBytes,
            occlusionBytes,
            retainedNativeCount,
            visibilityBit,
            cancellationToken);
        terrainBytes = TfragTerrainComposer.FinalizeTerrain(
            GameId.UYA,
            terrainBytes,
            textureAssetIdsByGroup,
            terrainTextures,
            occlusion.FirstCustomMappingIndex,
            cancellationToken);
        return new(
            terrainBytes,
            occlusion.Changed ? occlusion.OcclusionBytes : null,
            occlusion.VisibilityBit);
    }
}
