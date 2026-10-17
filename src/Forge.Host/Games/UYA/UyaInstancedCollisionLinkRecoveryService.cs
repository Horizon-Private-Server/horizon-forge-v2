using Forge.Host.Domain;
using RatchetPs2.Core.Games;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.UYA;

internal static class UyaInstancedCollisionLinkRecoveryService
{
    public static bool Recover(
        IList<ProjectEntity> entities,
        IReadOnlyList<UyaBaseLayerPayload> payloads,
        AssetCatalogStore catalog,
        IReadOnlySet<AssetId>? ignoredAssetIds = null)
    {
        var sourceGroups = entities.Where(entity => entity.Asset?.Kind is AssetKind.Tie or AssetKind.Shrub)
            .Where(entity => ignoredAssetIds is null || !ignoredAssetIds.Contains(entity.Asset!.Id))
            .GroupBy(entity => (entity.Asset!.Id, entity.Asset.Kind)).ToArray();
        var collisionIndexes = entities.Select((entity, index) => (entity, index))
            .Where(value => value.entity.Collision?.Kind == ProjectCollisionPieceKind.Solid)
            .ToDictionary(
                value => (value.entity.Collision!.SourcePayloadIndex, value.entity.Collision.SourcePieceIndex),
                value => value.index);
        if (collisionIndexes.Count == 0) return true;
        var collisionPayloads = payloads.Where(value => value.Layer == BakeLayerId.Collision).ToArray();
        var payloadIndexes = collisionPayloads.Select(value => value.SourceIndex).ToHashSet();
        if (collisionIndexes.Keys.Any(value => !payloadIndexes.Contains(value.SourcePayloadIndex))) return false;
        var tieSources = new List<CollisionTieGroup>();
        var shrubSources = new List<CollisionShrubGroup>();
        foreach (var group in sourceGroups)
        {
            var path = catalog.ResolveBlobPath(group.Key.Id);
            var entry = catalog.Query(new(Id: group.Key.Id)).SingleOrDefault();
            if (path is null || entry?.Kind != group.Key.Kind) return false;
            try
            {
                var length = new FileInfo(path).Length;
                if (length != entry.Size || length is <= 0 or > ForgeProjectWorkspace.MaxAttachedAssetBytes)
                    return false;
                var canonicalBytes = File.ReadAllBytes(path);
                if (AssetId.Compute(entry.Kind, entry.CanonicalFormatVersion, canonicalBytes) != entry.Id) return false;
                var modelBytes = UyaCanonicalAssetCodec.Decode(canonicalBytes).ModelBytes;
                var instances = group.Select(entity => new CollisionInstance(
                    entity.EntityId.ToString(),
                    UyaInstancedCollisionCompositionService.ToCollisionTransform(entity.Transform))).ToArray();
                if (group.Key.Kind == AssetKind.Tie) tieSources.Add(new(modelBytes, instances));
                else shrubSources.Add(new(modelBytes, instances));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or InvalidDataException or ArgumentException)
            {
                return false;
            }
        }

        var recovered = collisionIndexes.Keys.ToDictionary(
            value => value, _ => (IReadOnlyList<EntityId>)[]);
        foreach (var payload in collisionPayloads)
        {
            (EntityId ParentEntityId, int SourcePieceIndex, float Confidence)[] links;
            try
            {
                links = CollisionWork.FindTieCollisionLinks(
                        payload.Bytes, GameId.UYA, tieSources)
                    .Concat(CollisionWork.FindShrubCollisionCandidates(
                        payload.Bytes, GameId.UYA, shrubSources))
                    .Select(value => (
                        EntityId.Parse(value.InstanceId),
                        value.SourcePieceIndex,
                        value.Confidence))
                    .ToArray();
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException)
            {
                return false;
            }

            foreach (var group in links.GroupBy(value => value.SourcePieceIndex))
            {
                var key = (payload.SourceIndex, group.Key);
                if (!collisionIndexes.ContainsKey(key)) continue;
                recovered[key] = group
                    .OrderByDescending(value => value.Confidence)
                    .ThenBy(value => value.ParentEntityId.ToString(), StringComparer.Ordinal)
                    .Select(value => value.ParentEntityId)
                    .Distinct()
                    .Take(ForgeProjectValidation.MaxLinkedCollisionInstances)
                    .ToArray();
            }
        }
        foreach (var (key, links) in recovered)
        {
            var match = collisionIndexes[key];
            var collision = entities[match];
            entities[match] = collision with
            {
                Collision = collision.Collision! with { LinkedInstanceEntityIds = links },
            };
        }
        return true;
    }
}
