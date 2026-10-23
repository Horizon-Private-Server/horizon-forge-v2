using System.Security.Cryptography;
using System.Text;
using Forge.Host.Domain;

namespace Forge.Host.Games.UYA;

internal sealed record UyaCollisionContentImportSource(
    IReadOnlyList<ProjectEntity> Entities,
    IReadOnlyList<UyaBaseLayerPayload> BaseLayers);

internal static class UyaCollisionContentImportService
{
    internal static async Task<ContentImportBatchCommit> PrepareCommitAsync(
        ForgeProjectWorkspace workspace,
        AssetCatalogStore catalog,
        UyaCollisionContentImportSource source,
        ContentImportPlan plan,
        long? importedUnixMilliseconds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(plan);
        var family = UyaContentImportValidation.RequireSingleFamily(
            workspace, plan, "collision", ContentImportMode.AddAndReplace,
            "This importer accepts one add or replace collision family.");

        var payloads = source.BaseLayers.Where(value => value.Layer == BakeLayerId.Collision)
            .ToDictionary(value => value.SourceIndex);
        var donor = source.Entities.Where(value => value.Collision is not null).ToArray();
        if (payloads.Count == 0 || donor.Length == 0)
            throw new InvalidDataException("The donor collision inventory is empty.");
        foreach (var entity in donor)
        {
            var collision = entity.Collision!;
            if (entity.Asset is not { Kind: AssetKind.Collision } asset
                || entity.Provenance is not { Game: "UYA" }
                || !payloads.TryGetValue(collision.SourcePayloadIndex, out var payload)
                || AssetId.Compute(AssetKind.Collision, UyaBaseLayerSchema.CanonicalFormatVersion, payload.Bytes)
                    != asset.Id)
                throw new InvalidDataException("The donor collision inventory has an invalid source identity.");
        }

        var targetEntities = workspace.Content.Entities.ToDictionary(value => value.EntityId);
        var removed = family.Remove.Select(value =>
        {
            if (!value.StartsWith("entity:", StringComparison.Ordinal))
                throw new InvalidDataException("Collision replacement contains an invalid target ID.");
            var id = EntityId.Parse(value["entity:".Length..]);
            if (!targetEntities.TryGetValue(id, out var entity) || entity.Collision is null)
                throw new InvalidOperationException("The import plan references a missing target collision entity.");
            return id;
        }).ToHashSet();
        var retained = workspace.Content.Entities.Where(value => !removed.Contains(value.EntityId)).ToArray();
        var retainedKeys = retained.Where(value => value.Collision is not null && value.Asset is not null)
            .ToDictionary(CollisionKey, value => value.EntityId);
        var donorById = source.Entities.ToDictionary(value => value.EntityId);
        var retainedIds = retained.Select(value => value.EntityId).ToHashSet();
        var imported = new List<ProjectEntity>();
        var reusedEntities = new List<EntityId>();
        foreach (var entity in donor.OrderBy(value => value.Collision!.SourcePayloadIndex)
                     .ThenBy(value => value.Collision!.Kind)
                     .ThenBy(value => value.Collision!.SourcePieceIndex))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (retainedKeys.TryGetValue(CollisionKey(entity), out var retainedId))
            {
                reusedEntities.Add(retainedId);
                continue;
            }
            var collision = entity.Collision!;
            imported.Add(entity with
            {
                EntityId = new(StableGuid($"{plan.BatchId}\nentity\ncollision\n"
                    + $"{collision.SourcePayloadIndex}\n{collision.Kind}\n{collision.SourcePieceIndex}\n{entity.Asset!.Id}")),
                Name = $"Imported {entity.Name}",
                State = null,
                Collision = collision with
                {
                    SourcePayloadIndex = 0,
                    LinkedInstanceEntityIds = (collision.LinkedInstanceEntityIds ?? [])
                        .Select(MapLinkedInstance).OfType<EntityId>().Distinct().ToArray(),
                },
            });
        }

        var importedAssets = imported.Select(value => value.Asset!.Id).ToHashSet();
        var existingAssets = workspace.Content.Assets.Select(value => value.Id).ToHashSet();
        var attachments = payloads.Values.Select(payload => new
            {
                Id = AssetId.Compute(AssetKind.Collision, UyaBaseLayerSchema.CanonicalFormatVersion, payload.Bytes),
                payload.Bytes,
            })
            .Where(value => importedAssets.Contains(value.Id)
                && !existingAssets.Contains(value.Id))
            .GroupBy(value => value.Id)
            .Select(group => group.First())
            .Select(value => new ProjectAssetAttachment(
                AssetKind.Collision, UyaBaseLayerSchema.CanonicalFormatVersion, value.Bytes))
            .ToArray();
        var content = workspace.Content with
        {
            Entities = retained.Concat(imported).Select(value =>
                ProjectReferences.ClearNullableEntityReferences(value, removed)).ToArray(),
        };
        var replacementSourceAssetId = family.Mode == ContentImportMode.Replace
            && payloads.TryGetValue(0, out var primaryPayload)
                ? AssetId.Compute(
                    AssetKind.Collision, UyaBaseLayerSchema.CanonicalFormatVersion, primaryPayload.Bytes)
                : (AssetId?)null;
        await UyaBaseLayerStore.ValidateCollisionAsync(
            workspace.CreateProjection(content), catalog, replacementSourceAssetId, cancellationToken);

        var batch = new ContentImportBatch(
            ContentImportBatchSchema.CurrentVersion,
            plan.BatchId,
            plan.SchemaVersion,
            plan.CapabilitySchemaVersion,
            importedUnixMilliseconds ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            plan.Game,
            plan.Region,
            plan.Revision,
            plan.SourceFingerprint,
            plan.SourceLevelHash,
            plan.SourceLevel,
            [new("collision", family.Mode)],
            imported.Select(value => $"entity:{value.EntityId}")
                .Concat(attachments.Select(value => $"asset:{AssetId.Compute(
                    value.Kind, value.CanonicalFormatVersion, value.CanonicalBytes.Span)}"))
                .Order(StringComparer.Ordinal).ToArray(),
            reusedEntities.Select(value => $"entity:{value}")
                .Concat(importedAssets.Where(existingAssets.Contains)
                    .Select(value => $"asset:{value}"))
                .Order(StringComparer.Ordinal).ToArray(),
            removed.Select(value => $"entity:{value}").Order(StringComparer.Ordinal).ToArray(),
            family.Protect);
        return new(plan.TargetFingerprint, batch, content, attachments);

        EntityId? MapLinkedInstance(EntityId sourceId)
        {
            if (!donorById.TryGetValue(sourceId, out var owner)
                || owner.Layer is not ("ties" or "shrubs"))
                return null;
            var importedId = UyaStaticContentImportService.ImportedEntityId(
                plan.BatchId, owner.Layer, owner);
            return retainedIds.Contains(importedId) ? importedId : null;
        }
    }

    private static (AssetId AssetId, ProjectCollisionPieceKind Kind, int SourcePieceIndex) CollisionKey(
        ProjectEntity entity) => (entity.Asset!.Id, entity.Collision!.Kind, entity.Collision.SourcePieceIndex);

    private static Guid StableGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value))[..16];
        bytes[7] = (byte)((bytes[7] & 0x0f) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new(bytes);
    }
}
