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
            && state.CustomImports.Count == 0 && state.ImportedOwners.Count == 0)
            return new(terrainBytes, null, visibilityBit);

        var retainedNativeCount = workspace.Content.Entities.Count(entity => entity.Tfrag is
        {
            SourceKind: ProjectTfragSourceKind.Primary,
            SourceIndex: 0,
            CustomImportId: null,
            ImportedOwnerId: null,
        });
        var importedOcclusion = await UyaOcclusionImportPayload.ReadAsync(workspace, cancellationToken);
        if (importedOcclusion is not null)
        {
            var sourceLevel = workspace.Content.Occlusion!.SourceLevel;
            var firstOwnerId = workspace.Content.Entities.FirstOrDefault(entity => entity.Tfrag is
                { SourceKind: ProjectTfragSourceKind.Primary, SourceIndex: 0, GroupIndex: 0 })
                ?.Tfrag?.ImportedOwnerId;
            var owner = state.ImportedOwners.SingleOrDefault(value => value.SourceLevel == sourceLevel
                && value.SourceKind == ProjectTfragSourceKind.Primary && value.SourceIndex == 0
                && value.TargetKind == ProjectTfragSourceKind.Primary && value.TargetIndex == 0
                && value.OwnerId == firstOwnerId);
            // Keep donor slots unless primary terrain starts with the matching donor owner.
            if (owner is null)
                return new(TfragTerrainComposer.FinalizeTerrain(
                    GameId.UYA, terrainBytes, textureAssetIdsByGroup, terrainTextures,
                    firstOcclusionMappingIndex: null, cancellationToken), null, visibilityBit);
            sourceTerrainBytes = await workspace.ReadAttachedAssetVerifiedAsync(
                owner.NativePayload, ForgeProjectWorkspace.MaxAttachedAssetBytes, cancellationToken);
            retainedNativeCount = UyaBaseLayerService.ReadPrimaryTerrain(sourceTerrainBytes)?.TfragCount
                ?? throw new InvalidDataException("Imported occlusion primary tfrag payload is empty.");
        }
        var stagedOcclusionPath = Path.Combine(stagedTieRoot, "occlusion.bin");
        var occlusionBytes = File.Exists(stagedOcclusionPath)
            ? await File.ReadAllBytesAsync(stagedOcclusionPath, cancellationToken)
            : importedOcclusion?.GameplayMappings
                ?? sourcePackage.Files.Single(value => value.Path == "gameplay/core/occlusion.bin").Bytes;
        var donorOwners = state.ImportedOwners.Where(owner => owner.SourceLevel == workspace.Content.Occlusion?.SourceLevel)
            .Select(owner => owner.OwnerId).ToHashSet(StringComparer.Ordinal);
        var appendedMappings = importedOcclusion is null ? null : workspace.Content.Entities
            .Where(entity => entity.Tfrag is { SourceKind: ProjectTfragSourceKind.Primary, SourceIndex: 0 })
            .OrderBy(entity => entity.Tfrag!.GroupIndex).Skip(retainedNativeCount)
            .Select(entity => entity.Tfrag is { ImportedOwnerId: { } ownerId, OcclusionIndexStash: 0 } piece
                && donorOwners.Contains(ownerId) ? (int?)piece.OcclusionIndex : null).ToArray();
        var occlusion = TfragTerrainComposer.ComposeOcclusionMappings(
            GameId.UYA,
            terrainBytes,
            sourceTerrainBytes,
            occlusionBytes,
            retainedNativeCount,
            visibilityBit,
            cancellationToken,
            appendedSourceMappingIndices: appendedMappings);
        terrainBytes = TfragTerrainComposer.FinalizeTerrain(
            GameId.UYA,
            terrainBytes,
            textureAssetIdsByGroup,
            terrainTextures,
            occlusion.FirstCustomMappingIndex,
            cancellationToken,
            firstGeneratedGroupIndex: occlusion.FirstCustomGroupIndex);
        return new(
            terrainBytes,
            occlusion.Changed ? occlusion.OcclusionBytes : null,
            occlusion.VisibilityBit);
    }
}
