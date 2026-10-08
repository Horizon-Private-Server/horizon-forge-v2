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

    public static async Task<CollisionSource> ReadPrimaryAsync(
        ForgeProjectWorkspace workspace,
        AssetCatalogStore catalog,
        EntityId? replacementEntityId,
        CancellationToken cancellationToken)
    {
        var primaryEntities = workspace.Content.Entities
            .Where(entity => entity.Collision?.SourcePayloadIndex == 0
                && entity.Asset?.Kind == AssetKind.Collision)
            .ToArray();
        var assetIds = primaryEntities.Select(entity => entity.Asset!.Id).Distinct().ToArray();
        if (assetIds.Length != 1)
            throw new InvalidDataException(
                $"Combined analysis requires one primary collision payload; found {assetIds.Length}.");
        var bytes = await ReadAssetAsync(
            workspace, catalog, assetIds[0], AssetKind.Collision, cancellationToken);
        return new(
            bytes,
            CreateEdits(workspace, assetIds[0], bytes, sourcePayloadIndex: 0),
            BuildLinkedAdditions(
                workspace, assetIds[0], bytes, sourcePayloadIndex: 0, replacementEntityId, cancellationToken));
    }

    public static IReadOnlyList<CollisionPieceEdit> CreateEdits(
        ForgeProjectWorkspace workspace,
        AssetId assetId,
        byte[] source,
        int? sourcePayloadIndex = null)
    {
        var pieces = CollisionConverter.Inspect(source, GameId.UYA).Pieces;
        var sourceKeys = pieces.Select(value =>
            (UyaCollisionAdapter.ToProjectKind(value.Kind), value.SourcePieceIndex)).ToHashSet();
        var entities = workspace.Content.Entities.Where(value =>
                value.Asset?.Id == assetId
                && value.Collision is not null
                && (sourcePayloadIndex is null || value.Collision.SourcePayloadIndex == sourcePayloadIndex))
            .ToDictionary(value => (value.Collision!.Kind, value.Collision.SourcePieceIndex));
        if (entities.Keys.Any(key => !sourceKeys.Contains(key)))
            throw new InvalidDataException($"Collision asset {assetId} contains an unknown project piece.");
        return pieces.Select(piece =>
        {
            var kind = UyaCollisionAdapter.ToProjectKind(piece.Kind);
            if (!entities.TryGetValue((kind, piece.SourcePieceIndex), out var entity)
                || entity.State?.Disabled == true || entity.Collision!.Attachment is not null)
                return new CollisionPieceEdit(piece.Kind, piece.SourcePieceIndex, 0, 0, 0, Remove: true);
            return new CollisionPieceEdit(
                piece.Kind,
                piece.SourcePieceIndex,
                entity.Transform.Position.X,
                entity.Transform.Position.Y,
                entity.Transform.Position.Z);
        }).ToArray();
    }

    public static IReadOnlyList<CollisionSolidAddition> BuildLinkedAdditions(
        ForgeProjectWorkspace workspace,
        AssetId assetId,
        byte[] source,
        int? sourcePayloadIndex,
        EntityId? replacementEntityId,
        CancellationToken cancellationToken)
    {
        var linked = workspace.Content.Entities.Where(value =>
                value.Asset?.Id == assetId
                && value.Collision?.Attachment is not null
                && (sourcePayloadIndex is null || value.Collision.SourcePayloadIndex == sourcePayloadIndex)
                && value.State?.Disabled != true)
            .OrderBy(value => value.EntityId.ToString(), StringComparer.Ordinal)
            .ToArray();
        if (linked.Length == 0) return [];
        var entities = workspace.Content.Entities.ToDictionary(value => value.EntityId);
        var bindings = workspace.Content.InstancedCollisionBindings
            .Select(value => (value.SourceAssetId, value.InstanceEntityId))
            .ToHashSet();
        var pieces = CollisionWork.DecodeSolidPieces(source, GameId.UYA, cancellationToken)
            .ToDictionary(value => value.SourcePieceIndex);
        return linked.Select(value =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attachment = value.Collision!.Attachment!;
                var parent = entities[attachment.ParentEntityId];
                if (parent.State?.Disabled == true) return null;
                var bindingEntityId = parent.InstancedCollisionEnabled == false ? parent.EntityId : (EntityId?)null;
                if (parent.EntityId == replacementEntityId
                    || parent.InstancedCollisionEnabled is not null
                    && parent.Asset is { } parentAsset && parentAsset.IsInstancedCollisionSource()
                    && bindings.Contains((parentAsset.Id, bindingEntityId)))
                    return null;
                var id = $"linked:{value.EntityId}";
                var addition = new CollisionSolidAddition(id, pieces[value.Collision.SourcePieceIndex].Faces);
                return CollisionWork.TransformAdditionRelative(
                    addition,
                    GameId.UYA,
                    id,
                    ToCollisionTransform(value.Transform),
                    ToCollisionTransform(attachment.BindTransform),
                    ToCollisionTransform(parent.Transform),
                    cancellationToken);
            })
            .OfType<CollisionSolidAddition>()
            .ToArray();
    }

    public static async Task<IReadOnlyList<CollisionSolidAddition>> BuildAdditionsAsync(
        ForgeProjectWorkspace workspace,
        AssetCatalogStore catalog,
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

        var entities = workspace.Content.Entities.Where(entity =>
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
                        entity.InstancedCollisionEnabled == false ? entity.EntityId : null))))
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

    public static async Task<byte[]> ReadAssetAsync(
        ForgeProjectWorkspace workspace,
        AssetCatalogStore catalog,
        AssetId assetId,
        AssetKind kind,
        CancellationToken cancellationToken)
    {
        var path = workspace.ResolveAssetPath(assetId, catalog)
            ?? throw new FileNotFoundException($"Asset blob {assetId} is missing.");
        var attached = workspace.Content.Assets.SingleOrDefault(asset => asset.Id == assetId);
        if (attached is not null)
        {
            if (attached.Kind != kind) throw new InvalidDataException($"Asset {assetId} has the wrong kind.");
            return await AssetCatalogBlobReader.ReadVerifiedAsync(
                attached.Id, attached.Kind, attached.CanonicalFormatVersion, attached.Size,
                path, UyaAssetLimits.MaxCanonicalBytes, cancellationToken);
        }
        var entry = catalog.Query(new(Id: assetId)).SingleOrDefault()
            ?? throw new FileNotFoundException($"Asset {assetId} is not present in the catalog.");
        if (entry.Kind != kind) throw new InvalidDataException($"Asset {assetId} has the wrong kind.");
        return await AssetCatalogBlobReader.ReadVerifiedAsync(
            entry, path, UyaAssetLimits.MaxCanonicalBytes, cancellationToken);
    }

    internal static CollisionInstanceTransform ToCollisionTransform(ProjectTransform transform) => new(
        new(transform.Position.X, transform.Position.Y, transform.Position.Z),
        new(transform.Rotation.X, transform.Rotation.Y, transform.Rotation.Z, transform.Rotation.W),
        new(transform.Scale.X, transform.Scale.Y, transform.Scale.Z));
}
