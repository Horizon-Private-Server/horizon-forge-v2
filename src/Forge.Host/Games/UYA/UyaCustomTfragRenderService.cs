using System.Security.Cryptography;
using Forge.Host.Domain;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.IO;
using RatchetPs2.Core.LevelAssets;
using RatchetPs2.Core.Tfrags;
using RatchetPs2.Core.Wad;
using RatchetPs2.Core.Wad.Models;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.UYA;

internal static class UyaCustomTfragRenderService
{
    internal static string? CreateRevision(ForgeProjectWorkspace workspace)
    {
        var customEntities = workspace.Content.Entities
            .Where(entity => entity.Asset is { Kind: AssetKind.Tfrag }
                && entity.Tfrag?.CustomImportId is not null)
            .OrderBy(entity => entity.Tfrag!.SourceIndex)
            .ThenBy(entity => entity.Tfrag!.GroupIndex)
            .ToArray();
        if (customEntities.Length == 0) return null;
        var textures = workspace.Content.Tfrags?.CustomImports
            .SelectMany(value => value.Textures)
            .Select(value => value.Id.ToString())
            .Order(StringComparer.Ordinal)
            .ToArray() ?? [];
        var identity = ForgeProjectPersistence.Serialize(new
        {
            CompositionVersion = TfragPieceConverter.CompositionVersion,
            NativeTerrainGenerationVersion = StaticModelTfragConverter.GetNativeTerrainGenerationVersion(GameId.UYA),
            Pieces = customEntities.Select(entity => new
            {
                entity.EntityId,
                AssetId = entity.Asset!.Id,
                entity.Tfrag!.SourceIndex,
                entity.Tfrag.GroupIndex,
                entity.Tfrag.SourcePosition,
            }).ToArray(),
            Textures = textures,
        });
        return Convert.ToHexString(SHA256.HashData(identity)).ToLowerInvariant()[..16];
    }

    internal static async Task<PackedFilePackage> BuildAsync(
        byte[] levelWad,
        int level,
        ForgeProjectWorkspace workspace,
        AssetCatalogStore catalog,
        CancellationToken cancellationToken)
    {
        if (workspace.Content.Tfrags?.CustomImports.Count is not > 0)
            return await Task.Run(() => FrontendMapPackageBuilder.BuildLevelWadPart(
                levelWad, GameId.UYA, FrontendMapAssetGroup.Terrain), cancellationToken);

        var inspection = await UyaBaseLayerStore.InspectAsync(
            workspace, catalog, BakeLayerId.Tfrags, cancellationToken);
        if (inspection.Manifest is null || inspection.Blockers[BakeLayerId.Tfrags].Count > 0)
            throw new InvalidDataException(string.Join(' ', inspection.Blockers[BakeLayerId.Tfrags]));
        var record = inspection.Manifest.Layers.Single(value => value.Layer == BakeLayerId.Tfrags);
        var compositions = await UyaTfragBakeService.ComposePreviewAsync(
            workspace, catalog, record, cancellationToken);
        if (!compositions.TryGetValue("primary.bin", out var terrain))
            throw new InvalidDataException("Custom terrain composition did not produce the primary terrain payload.");

        var sourceAssets = LevelArchiveReader.ReadAssetSourceFiles(GameId.UYA, levelWad);
        var customTextures = await UyaCustomTfragTextureService.ComposeAsync(
            workspace, terrain, cancellationToken);

        var sourceAssetPayload = BinaryMagic.IsWad(sourceAssets.AssetWadBytes)
            ? WadCompression.Decompress(sourceAssets.AssetWadBytes, new(), cancellationToken)
            : sourceAssets.AssetWadBytes;
        var staticComposition = StaticAssetComposer.AppendTerrainTextures(
            GameId.UYA,
            sourceAssets.HeaderBytes,
            sourceAssetPayload,
            sourceAssets.PaletteBytes,
            customTextures.Textures,
            cancellationToken);
        terrain = TfragTerrainComposer.FinalizeTerrain(
            GameId.UYA,
            terrain,
            customTextures.TextureAssetIdsByGroup,
            staticComposition.TerrainTextures,
            firstOcclusionMappingIndex: null,
            cancellationToken);
        var composed = LevelAssetComposer.ComposeAssetWad(
            GameId.UYA,
            staticComposition.HeaderBytes,
            staticComposition.AssetWadBytes,
            new(Terrain: terrain),
            cancellationToken);
        var chunks = sourceAssets.ChunkWads.ToDictionary(value => value.Key, value => value.Value);
        foreach (var (name, bytes) in compositions)
        {
            if (!name.StartsWith("chunk", StringComparison.Ordinal)
                || !name.EndsWith(".bin", StringComparison.Ordinal)
                || !int.TryParse(name.AsSpan(5, name.Length - 9), out var index))
                continue;
            if (!chunks.TryGetValue(index, out var sourceChunk))
                throw new InvalidDataException($"Custom terrain composition references missing chunk {index}.");
            chunks[index] = LevelAssetComposer.ComposeTfragChunk(
                GameId.UYA, sourceChunk, bytes, cancellationToken: cancellationToken);
        }

        return await Task.Run(() => FrontendMapPackageBuilder.BuildAssetPackage(
            GameId.UYA,
            level,
            composed.HeaderBytes,
            staticComposition.PaletteBytes,
            composed.AssetWadBytes,
            chunks,
            FrontendMapAssetGroup.Terrain), cancellationToken);
    }
}
