using Forge.Host.Domain;
using RatchetPs2.Core.Games;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.UYA;

internal static class UyaTieCollisionCompositionService
{
    internal sealed record CollisionSource(byte[] Bytes, IReadOnlyList<CollisionPieceEdit> Edits);

    public static async Task<CollisionSource> ReadPrimaryAsync(
        ForgeProjectWorkspace workspace,
        AssetCatalogStore catalog,
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
        return new(bytes, CreateEdits(workspace, assetIds[0], bytes, sourcePayloadIndex: 0));
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
                || entity.State?.Disabled == true)
                return new CollisionPieceEdit(piece.Kind, piece.SourcePieceIndex, 0, 0, 0, Remove: true);
            return new CollisionPieceEdit(
                piece.Kind,
                piece.SourcePieceIndex,
                entity.Transform.Position.X,
                entity.Transform.Position.Y,
                entity.Transform.Position.Z);
        }).ToArray();
    }

    public static async Task<IReadOnlyList<CollisionSolidAddition>> BuildAdditionsAsync(
        ForgeProjectWorkspace workspace,
        AssetCatalogStore catalog,
        AssetId? replacementTieAssetId,
        byte[]? replacementProxyBytes,
        IDictionary<AssetId, CollisionSolidAddition> decodedProxies,
        CancellationToken cancellationToken)
    {
        var bindings = workspace.Content.TieCollisionBindings.ToDictionary(value => value.TieAssetId);
        CollisionSolidAddition? replacement = null;
        if (replacementTieAssetId is not null)
        {
            ArgumentNullException.ThrowIfNull(replacementProxyBytes);
            replacement = CollisionWork.DecodeSolidAddition(
                replacementProxyBytes, GameId.UYA, "preview", cancellationToken);
        }

        var entities = workspace.Content.Entities.Where(entity =>
                entity.Asset is { Kind: AssetKind.Tie }
                && entity.State?.Disabled != true
                && entity.TieCollisionEnabled != false
                && (entity.Asset.Id == replacementTieAssetId || bindings.ContainsKey(entity.Asset.Id)))
            .OrderBy(entity => entity.EntityId.ToString(), StringComparer.Ordinal)
            .ToArray();
        var additions = new List<CollisionSolidAddition>(entities.Length);
        var verifiedTieAssets = new HashSet<AssetId>();
        foreach (var entity in entities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tieAssetId = entity.Asset!.Id;
            if (verifiedTieAssets.Add(tieAssetId))
                _ = await ReadAssetAsync(
                    workspace, catalog, tieAssetId, AssetKind.Tie, cancellationToken);
            CollisionSolidAddition addition;
            if (tieAssetId == replacementTieAssetId)
            {
                addition = replacement!;
            }
            else
            {
                var binding = bindings[tieAssetId];
                var proxyId = binding.ProxyAssetId;
                if (!decodedProxies.TryGetValue(proxyId, out addition!))
                {
                    var bytes = await ReadAssetAsync(
                        workspace, catalog, proxyId, AssetKind.Collision, cancellationToken);
                    addition = CollisionWork.DecodeSolidAddition(
                        bytes, GameId.UYA, proxyId.ToString(), cancellationToken);
                    decodedProxies.Add(proxyId, addition);
                }
                addition = addition with
                {
                    Faces = addition.Faces.Select(face => face with
                    {
                        RawType = binding.Recipe.RawType,
                    }).ToArray(),
                };
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

    private static CollisionInstanceTransform ToCollisionTransform(ProjectTransform transform) => new(
        new(transform.Position.X, transform.Position.Y, transform.Position.Z),
        new(transform.Rotation.X, transform.Rotation.Y, transform.Rotation.Z, transform.Rotation.W),
        new(transform.Scale.X, transform.Scale.Y, transform.Scale.Z));
}
