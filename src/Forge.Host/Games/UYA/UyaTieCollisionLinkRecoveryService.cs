using Forge.Host.Domain;
using RatchetPs2.Core.Games;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.UYA;

internal static class UyaTieCollisionLinkRecoveryService
{
    private const float MinimumConfidenceLead = 0.02f;

    public static void Recover(
        IList<ProjectEntity> entities,
        IReadOnlyList<UyaBaseLayerPayload> payloads,
        AssetCatalogStore catalog)
    {
        var tieGroups = entities.Where(entity => entity.Asset?.Kind == AssetKind.Tie)
            .GroupBy(entity => entity.Asset!.Id).ToArray();
        if (tieGroups.Length == 0) return;
        var tiesById = tieGroups.SelectMany(group => group).ToDictionary(entity => entity.EntityId);
        var collisionIndexes = entities.Select((entity, index) => (entity, index))
            .Where(value => value.entity.Collision?.Kind == ProjectCollisionPieceKind.Solid)
            .ToDictionary(
                value => (value.entity.Collision!.SourcePayloadIndex, value.entity.Collision.SourcePieceIndex),
                value => value.index);
        var sources = new List<CollisionTieGroup>();
        foreach (var group in tieGroups)
        {
            var path = catalog.ResolveBlobPath(group.Key);
            var entry = catalog.Query(new(Id: group.Key)).SingleOrDefault();
            if (path is null || entry?.Kind != AssetKind.Tie) continue;
            try
            {
                var length = new FileInfo(path).Length;
                if (length != entry.Size || length is <= 0 or > UyaAssetLimits.MaxCanonicalBytes) continue;
                var canonicalBytes = File.ReadAllBytes(path);
                if (AssetId.Compute(entry.Kind, entry.CanonicalFormatVersion, canonicalBytes) != entry.Id) continue;
                sources.Add(new(
                    UyaCanonicalAssetCodec.Decode(canonicalBytes).ModelBytes,
                    group.Select(entity => new CollisionTieInstance(
                        entity.EntityId.ToString(),
                        UyaTieCollisionCompositionService.ToCollisionTransform(entity.Transform))).ToArray()));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or InvalidDataException or ArgumentException)
            {
                // Recovery is best-effort; malformed or unsupported geometry remains ordinary collision.
            }
        }

        foreach (var payload in payloads.Where(value => value.Layer == BakeLayerId.Collision))
        {
            (EntityId TieEntityId, int SourcePieceIndex, float Confidence)[] candidates;
            try
            {
                candidates = CollisionWork.FindTieCollisionCandidates(
                        payload.Bytes, GameId.UYA, sources)
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
                var tie = tiesById[candidate.TieEntityId];
                entities[match] = collision with
                {
                    Collision = collision.Collision! with
                    {
                        Attachment = new(candidate.TieEntityId, tie.Transform),
                    },
                };
            }
        }
    }
}
