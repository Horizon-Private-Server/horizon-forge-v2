using Forge.Host.Domain;
using RatchetPs2.Core.Games;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.UYA;

internal static class UyaInstancedCollisionLinkRecoveryService
{
    private const float MinimumConfidenceLead = 0.02f;

    public static void Recover(
        IList<ProjectEntity> entities,
        IReadOnlyList<UyaBaseLayerPayload> payloads,
        AssetCatalogStore catalog)
    {
        var sourceGroups = entities.Where(entity => entity.Asset.IsInstancedCollisionSource())
            .GroupBy(entity => (entity.Asset!.Id, entity.Asset.Kind)).ToArray();
        if (sourceGroups.Length == 0) return;
        var sourcesById = sourceGroups.SelectMany(group => group).ToDictionary(entity => entity.EntityId);
        var collisionIndexes = entities.Select((entity, index) => (entity, index))
            .Where(value => value.entity.Collision?.Kind == ProjectCollisionPieceKind.Solid)
            .ToDictionary(
                value => (value.entity.Collision!.SourcePayloadIndex, value.entity.Collision.SourcePieceIndex),
                value => value.index);
        var tieSources = new List<CollisionTieGroup>();
        var shrubSources = new List<CollisionShrubGroup>();
        foreach (var group in sourceGroups)
        {
            var path = catalog.ResolveBlobPath(group.Key.Id);
            var entry = catalog.Query(new(Id: group.Key.Id)).SingleOrDefault();
            if (path is null || entry?.Kind != group.Key.Kind) continue;
            try
            {
                var length = new FileInfo(path).Length;
                if (length != entry.Size || length is <= 0 or > ForgeProjectWorkspace.MaxAttachedAssetBytes) continue;
                var canonicalBytes = File.ReadAllBytes(path);
                if (AssetId.Compute(entry.Kind, entry.CanonicalFormatVersion, canonicalBytes) != entry.Id) continue;
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
                // Recovery is best-effort; malformed or unsupported geometry remains ordinary collision.
            }
        }

        foreach (var payload in payloads.Where(value => value.Layer == BakeLayerId.Collision))
        {
            (EntityId ParentEntityId, int SourcePieceIndex, float Confidence)[] candidates;
            try
            {
                candidates = CollisionWork.FindTieCollisionCandidates(
                        payload.Bytes, GameId.UYA, tieSources)
                    .Concat(CollisionWork.FindShrubCollisionCandidates(
                        payload.Bytes, GameId.UYA, shrubSources))
                    .Select(value => (EntityId.Parse(value.InstanceId), value.SourcePieceIndex, value.Confidence))
                    .ToArray();
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException)
            {
                continue;
            }

            foreach (var group in candidates.GroupBy(value => value.SourcePieceIndex))
            {
                var ranked = group.OrderByDescending(value => value.Confidence).ToArray();
                if (ranked.Length > 1 && ranked[0].Confidence - ranked[1].Confidence < MinimumConfidenceLead)
                    continue;
                var candidate = ranked[0];
                var match = collisionIndexes[(payload.SourceIndex, candidate.SourcePieceIndex)];
                var collision = entities[match];
                var parent = sourcesById[candidate.ParentEntityId];
                entities[match] = collision with
                {
                    Collision = collision.Collision! with
                    {
                        Attachment = new(candidate.ParentEntityId, parent.Transform),
                    },
                };
            }
        }
    }
}
