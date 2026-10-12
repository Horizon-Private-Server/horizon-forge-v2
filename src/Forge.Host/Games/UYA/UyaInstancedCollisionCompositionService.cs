using Forge.Host.Domain;
using RatchetPs2.Core.Games;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.UYA;

internal static class UyaInstancedCollisionCompositionService
{
    internal sealed record CollisionSource(
        byte[] Bytes,
        IReadOnlyList<CollisionPieceEdit> Edits,
        IReadOnlyList<CollisionSolidAddition> Additions);

    public static async Task<CollisionSource> ReadAsync(
        ForgeProjectWorkspace workspace,
        AssetCatalogStore catalog,
        int sourcePayloadIndex,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sourcePayloadIndex);
        var inspection = await UyaBaseLayerStore.InspectAsync(
            workspace, catalog, BakeLayerId.Collision, cancellationToken);
        if (inspection.Manifest is null || inspection.Blockers[BakeLayerId.Collision].Count > 0)
            throw new InvalidDataException(string.Join(' ', inspection.Blockers[BakeLayerId.Collision]));
        var name = sourcePayloadIndex == 0 ? "collision.bin" : $"chunk-{sourcePayloadIndex}.bin";
        var asset = inspection.Manifest.Layers.Single(value => value.Layer == BakeLayerId.Collision)
            .Assets.SingleOrDefault(value => value.Name == name)
            ?? throw new InvalidDataException($"Collision source {sourcePayloadIndex} has no matching payload.");
        var bytes = await ReadAssetAsync(
            workspace, catalog, asset.Asset.Id, AssetKind.Collision, cancellationToken);
        return new(
            bytes,
            CreateCompleteState(workspace, asset.Asset.Id, sourcePayloadIndex),
            []);
    }

    public static IReadOnlyList<CollisionPieceEdit> CreateCompleteState(
        ForgeProjectWorkspace workspace,
        AssetId assetId,
        int? sourcePayloadIndex = null)
    {
        var entities = workspace.Content.Entities.Where(value =>
                value.Asset?.Id == assetId
                && value.Collision is not null
                && (sourcePayloadIndex is null || value.Collision.SourcePayloadIndex == sourcePayloadIndex))
            .ToDictionary(value => (value.Collision!.Kind, value.Collision.SourcePieceIndex));
        return entities.Values.OrderBy(value => value.Collision!.Kind)
            .ThenBy(value => value.Collision!.SourcePieceIndex)
            .Select(entity => new CollisionPieceEdit(
                UyaCollisionAdapter.ToSdkKind(entity.Collision!.Kind),
                entity.Collision.SourcePieceIndex,
                entity.Transform.Position.X,
                entity.Transform.Position.Y,
                entity.Transform.Position.Z,
                Remove: entity.State?.Disabled == true,
                Rotation: new(
                    entity.Transform.Rotation.X,
                    entity.Transform.Rotation.Y,
                    entity.Transform.Rotation.Z,
                    entity.Transform.Rotation.W)))
            .ToArray();
    }

    public static async Task<IReadOnlyList<CollisionSolidAddition>> BuildAdditionsAsync(
        ForgeProjectWorkspace workspace,
        AssetCatalogStore catalog,
        int sourcePayloadIndex,
        EntityId? replacementEntityId,
        AssetId? replacementSourceAssetId,
        byte[]? replacementProxyBytes,
        IDictionary<AssetId, CollisionSolidAddition> decodedProxies,
        CancellationToken cancellationToken)
    {
        var bindings = workspace.Content.InstancedCollisionBindings.ToDictionary(
            value => (value.SourceAssetId, value.InstanceEntityId));
        CollisionSolidAddition? replacement = null;
        var replacementShared = replacementEntityId is not null
            && workspace.Content.Entities.Single(value => value.EntityId == replacementEntityId).InstancedCollisionEnabled != false;
        if (replacementSourceAssetId is not null)
        {
            ArgumentNullException.ThrowIfNull(replacementProxyBytes);
            replacement = CollisionWork.DecodeSolidAddition(
                replacementProxyBytes, GameId.UYA, "preview", cancellationToken);
        }

        var entities = SelectEntities(
                workspace, bindings, replacementEntityId, replacementSourceAssetId)
            .Where(entity => SourcePayloadIndex(entity) == sourcePayloadIndex)
            .OrderBy(entity => entity.EntityId.ToString(), StringComparer.Ordinal)
            .ToArray();
        var additions = new List<CollisionSolidAddition>(entities.Length);
        var verifiedSourceAssets = new HashSet<AssetId>();
        var effectiveProxies = new Dictionary<(AssetId, EntityId?), CollisionSolidAddition>();
        foreach (var entity in entities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceAssetId = entity.Asset!.Id;
            if (verifiedSourceAssets.Add(sourceAssetId))
                _ = await ReadAssetAsync(
                    workspace, catalog, sourceAssetId, entity.Asset.Kind, cancellationToken);
            CollisionSolidAddition addition;
            if (entity.EntityId == replacementEntityId
                || replacementShared && entity.InstancedCollisionEnabled == true
                    && sourceAssetId == replacementSourceAssetId)
            {
                addition = replacement!;
            }
            else
            {
                var bindingKey = (
                    sourceAssetId,
                    entity.InstancedCollisionEnabled == false ? entity.EntityId : (EntityId?)null);
                if (!effectiveProxies.TryGetValue(bindingKey, out addition!))
                {
                    var binding = bindings[bindingKey];
                    var proxyId = binding.ProxyAssetId;
                    if (!decodedProxies.TryGetValue(proxyId, out addition!))
                    {
                        var bytes = await ReadAssetAsync(
                            workspace, catalog, proxyId, AssetKind.Collision, cancellationToken);
                        addition = CollisionWork.DecodeSolidAddition(
                            bytes, GameId.UYA, proxyId.ToString(), cancellationToken);
                        decodedProxies.Add(proxyId, addition);
                    }
                    if (binding.FaceTypeOverrides.Any(value => value.FaceIndex >= addition.Faces.Count))
                        throw new InvalidDataException(
                            $"Instanced collision proxy {proxyId} contains a face-type override outside its decoded topology.");
                    var faceTypes = binding.FaceTypeOverrides.ToDictionary(
                        value => value.FaceIndex, value => value.RawType);
                    addition = addition with
                    {
                        Faces = addition.Faces.Select((face, faceIndex) => face with
                        {
                            RawType = faceTypes.GetValueOrDefault(faceIndex, binding.Recipe.RawType),
                        }).ToArray(),
                    };
                    effectiveProxies.Add(bindingKey, addition);
                }
            }
            additions.Add(CollisionWork.TransformAddition(
                addition,
                GameId.UYA,
                entity.EntityId.ToString(),
                ToCollisionTransform(entity.Transform),
                cancellationToken));
        }
        return additions;
    }

    public static IReadOnlySet<int> RequiredSourcePayloadIndexes(ForgeProjectWorkspace workspace)
    {
        var bindings = workspace.Content.InstancedCollisionBindings.ToDictionary(
            value => (value.SourceAssetId, value.InstanceEntityId));
        return SelectEntities(workspace, bindings, null, null)
            .Select(SourcePayloadIndex)
            .ToHashSet();
    }

    private static IEnumerable<ProjectEntity> SelectEntities(
        ForgeProjectWorkspace workspace,
        IReadOnlyDictionary<(AssetId, EntityId?), ProjectInstancedCollisionBinding> bindings,
        EntityId? replacementEntityId,
        AssetId? replacementSourceAssetId) => workspace.Content.Entities.Where(entity =>
            entity.Asset.IsInstancedCollisionSource()
            && entity.State?.Disabled != true
            && (entity.EntityId == replacementEntityId
                || entity.InstancedCollisionEnabled is not null
                && bindings.ContainsKey((
                    entity.Asset.Id,
                    entity.InstancedCollisionEnabled == false ? entity.EntityId : null)))
            && (entity.Asset.Id == replacementSourceAssetId
                || bindings.ContainsKey((
                    entity.Asset.Id,
                    entity.InstancedCollisionEnabled == false ? entity.EntityId : null))));

    internal static int SourcePayloadIndex(ProjectEntity entity) => entity.Asset!.Kind switch
    {
        AssetKind.Tfrag when entity.Tfrag is not null => entity.Tfrag.SourceIndex,
        AssetKind.Tie or AssetKind.Shrub => 0,
        _ => throw new InvalidDataException($"Entity {entity.EntityId} has no collision payload owner."),
    };

    public static async Task<byte[]> ReadAssetAsync(
        ForgeProjectWorkspace workspace,
        AssetCatalogStore catalog,
        AssetId assetId,
        AssetKind kind,
        CancellationToken cancellationToken)
    {
        var resolved = workspace.ResolveAsset(new(assetId, kind), catalog)
            ?? throw new FileNotFoundException($"Asset blob {assetId} is missing.");
        return await AssetCatalogBlobReader.ReadVerifiedAsync(
            resolved.Effective.Id, resolved.Effective.Kind, resolved.CanonicalFormatVersion, resolved.Size,
            resolved.Path, ForgeProjectWorkspace.MaxAttachedAssetBytes, cancellationToken);
    }

    internal static CollisionInstanceTransform ToCollisionTransform(ProjectTransform transform) => new(
        new(transform.Position.X, transform.Position.Y, transform.Position.Z),
        new(transform.Rotation.X, transform.Rotation.Y, transform.Rotation.Z, transform.Rotation.W),
        new(transform.Scale.X, transform.Scale.Y, transform.Scale.Z));
}
