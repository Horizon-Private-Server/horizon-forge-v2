using Forge.Host.Domain;
using System.Text.Json;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.Skyboxes;
using RatchetPs2.Core.Tfrags;
using RatchetPs2.Games.UYA.Gameplay;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.UYA;

public static partial class UyaBaseLayerStore
{
    internal static async Task<IReadOnlyDictionary<string, byte[]>> ComposeCollisionAsync(
        ForgeProjectWorkspace workspace,
        AssetCatalogStore catalog,
        UyaBaseLayerRecord record,
        CancellationToken cancellationToken,
        AssetId? replacementSourceAssetId = null)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var sourcePayloadIndexes = record.Assets.Select(asset => CollisionSourceIndex(asset.Name)).ToHashSet();
        var missingSourcePayloadIndexes = UyaInstancedCollisionCompositionService
            .RequiredSourcePayloadIndexes(workspace)
            .Where(value => !sourcePayloadIndexes.Contains(value))
            .Order()
            .ToArray();
        if (missingSourcePayloadIndexes.Length > 0)
            throw new InvalidDataException(
                $"Generated collision targets missing source payloads: {string.Join(", ", missingSourcePayloadIndexes)}.");
        var decodedProxies = new Dictionary<AssetId, CollisionSolidAddition>();
        var decodedImports = new Dictionary<AssetId,
            (IReadOnlyDictionary<int, CollisionSolidPiece> Solids,
                IReadOnlyDictionary<int, CollisionPlayerBarrierPiece> Barriers)>();
        foreach (var asset in record.Assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourcePayloadIndex = CollisionSourceIndex(asset.Name);
            var collisionEntities = workspace.Content.Entities.Where(value =>
                    value.Collision?.SourcePayloadIndex == sourcePayloadIndex
                    && value.Asset is { Kind: AssetKind.Collision })
                .ToArray();
            var sourceAssetId = sourcePayloadIndex == 0
                ? replacementSourceAssetId
                    ?? LatestCollisionReplacementSource(workspace.Content, collisionEntities)
                    ?? asset.Asset.Id
                : asset.Asset.Id;
            if (!collisionEntities.Any(value => value.Asset!.Id == sourceAssetId))
                sourceAssetId = asset.Asset.Id;
            var source = sourceAssetId == asset.Asset.Id
                ? await File.ReadAllBytesAsync(workspace.ResolveAssetPath(asset.Asset, catalog)
                    ?? throw new FileNotFoundException($"Collision source {asset.Asset.Id} is missing."), cancellationToken)
                : await UyaInstancedCollisionCompositionService.ReadAssetAsync(
                    workspace, catalog, sourceAssetId, AssetKind.Collision, cancellationToken);
            var edits = UyaInstancedCollisionCompositionService.CreateCompleteState(
                workspace, sourceAssetId, sourcePayloadIndex);
            var assetAdditions = await UyaInstancedCollisionCompositionService.BuildAdditionsAsync(
                workspace,
                catalog,
                sourcePayloadIndex,
                replacementEntityId: null,
                replacementSourceAssetId: null,
                replacementProxyBytes: null,
                decodedProxies,
                cancellationToken);
            var importedSolids = new List<CollisionSolidAddition>();
            var importedBarriers = new List<CollisionPlayerBarrierPiece>();
            foreach (var entity in collisionEntities.Where(value => value.Asset!.Id != sourceAssetId)
                     .OrderBy(value => value.EntityId.ToString(), StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceAsset = entity.Asset!;
                if (!decodedImports.TryGetValue(sourceAsset.Id, out var decoded))
                {
                    var importedBytes = await UyaInstancedCollisionCompositionService.ReadAssetAsync(
                        workspace, catalog, sourceAsset.Id, AssetKind.Collision, cancellationToken);
                    decoded = (
                        CollisionWork.DecodeSolidPieces(importedBytes, GameId.UYA, cancellationToken)
                            .ToDictionary(value => value.SourcePieceIndex),
                        CollisionWork.DecodePlayerBarrierPieces(importedBytes, GameId.UYA, cancellationToken)
                            .ToDictionary(value => value.SourcePieceIndex));
                    decodedImports.Add(sourceAsset.Id, decoded);
                }
                var transform = UyaInstancedCollisionCompositionService.ToCollisionTransform(entity.Transform);
                if (entity.Collision!.Kind == ProjectCollisionPieceKind.Solid)
                {
                    var piece = decoded.Solids.GetValueOrDefault(entity.Collision.SourcePieceIndex)
                        ?? throw new InvalidDataException(
                            $"Imported collision {entity.EntityId} source piece is missing.");
                    var addition = CollisionWork.TransformAddition(
                        new(entity.EntityId.ToString(), piece.Faces), GameId.UYA,
                        entity.EntityId.ToString(), transform, cancellationToken);
                    if (addition.Faces.Count == 0)
                        throw new InvalidDataException(
                            $"Imported collision {entity.EntityId} source piece "
                            + $"{entity.Collision.SourcePieceIndex} contains no representable faces.");
                    importedSolids.Add(addition);
                }
                else
                {
                    var piece = decoded.Barriers.GetValueOrDefault(entity.Collision.SourcePieceIndex)
                        ?? throw new InvalidDataException(
                            $"Imported player barrier {entity.EntityId} source piece is missing.");
                    importedBarriers.Add(CollisionWork.TransformPlayerBarrierPiece(
                        piece, GameId.UYA, transform, cancellationToken));
                }
            }
            var composition = await Task.Run(
                () => CollisionWork.ComposeComplete(
                    source, GameId.UYA, edits, assetAdditions.Concat(importedSolids).ToArray(),
                    importedBarriers, cancellationToken),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(asset.Name, composition.Bytes);
        }
        return result;
    }

    private static AssetId? LatestCollisionReplacementSource(
        ForgeProjectContent content,
        IReadOnlyList<ProjectEntity> collisionEntities)
    {
        foreach (var batch in content.ImportBatches.Reverse().Where(batch =>
                     batch.Families.Any(value => value is { Family: "collision", Mode: ContentImportMode.Replace })))
        {
            var added = batch.Added.ToHashSet(StringComparer.Ordinal);
            var assets = collisionEntities.Where(value =>
                    added.Contains($"entity:{value.EntityId}")
                    && value.Provenance?.Section.StartsWith("collision/primary/", StringComparison.Ordinal) == true)
                .Select(value => value.Asset!.Id).Distinct().ToArray();
            if (assets.Length == 1) return assets[0];
        }
        return null;
    }

}
