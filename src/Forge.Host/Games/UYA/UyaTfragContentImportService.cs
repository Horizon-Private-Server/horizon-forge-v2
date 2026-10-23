using System.Security.Cryptography;
using System.Text;
using Forge.Host.Domain;

namespace Forge.Host.Games.UYA;

public sealed record UyaTfragContentImportRequest(
    string ProjectPath,
    string CatalogRootPath,
    string SourceIsoPath,
    string SourceFingerprint,
    int SourceLevel,
    string BatchId,
    ContentImportMode Mode);

internal sealed record UyaTfragContentImportSource(
    UyaTfragProjectImport Tfrags,
    IReadOnlyList<UyaBaseLayerPayload> BaseLayers,
    IReadOnlyList<UyaTerrainTextureSource> TerrainTextures);

internal static class UyaTfragContentImportService
{
    private const uint NativeOwnerFormatVersion = 0;

    public static async Task<UyaStaticContentImportResult> ImportAsync(
        EditorRuntime runtime,
        UyaTfragContentImportRequest request,
        Func<IsoProgress, ValueTask>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Mode is not (ContentImportMode.Add or ContentImportMode.Replace))
            throw new ArgumentException("Tfrag import requires add or replace mode.", nameof(request));
        var context = await UyaContentImportPlanningService.PrepareAsync(new(
            request.ProjectPath, request.CatalogRootPath, request.SourceIsoPath,
            request.SourceFingerprint, request.SourceLevel, [new("tfrags", request.Mode)]),
            progress, cancellationToken);
        if (context.Plan.BatchId != request.BatchId)
            throw new InvalidOperationException("The import plan changed; review the refreshed plan before importing.");
        var command = PrepareCommit(context.Workspace, new(
            context.Source.Tfrags, context.Source.BaseLayers, context.Source.TerrainTextures),
            context.Plan, cancellationToken: cancellationToken);
        await runtime.CommitContentImportAsync(command, cancellationToken);
        var family = context.Plan.Families.Single(value => value.Family == "tfrags");
        return new(command.Batch.BatchId, command.Batch.Added.Count(value => value.StartsWith("entity:")),
            command.Batch.Replaced.Count, family.Protect.Count, command.Attachments.Count);
    }

    internal static ContentImportBatchCommit PrepareCommit(
        ForgeProjectWorkspace workspace,
        UyaTfragContentImportSource source,
        ContentImportPlan plan,
        long? importedUnixMilliseconds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(plan);
        if (workspace.Content.Tfrags is null)
            throw new InvalidDataException("The target project has no tfrag inventory.");
        UyaContentImportValidation.ValidatePlan(workspace, plan);
        var family = plan.Families.SingleOrDefault(value => value.Selected);
        if (family is null || family.Family != "tfrags"
            || plan.Families.Count(value => value.Selected) != 1)
            throw new InvalidOperationException("This importer accepts only the tfrag family.");

        var removed = RemovalSet(workspace, family);
        var retained = workspace.Content.Entities.Where(value => !removed.Contains(value.EntityId)).ToList();
        var retainedIds = retained.Select(value => value.EntityId).ToHashSet();
        var attachments = new Dictionary<AssetId, ProjectAssetAttachment>();
        var importedOwners = workspace.Content.Tfrags.ImportedOwners
            .Where(value => value.Pieces.All(retainedIds.Contains)).ToList();
        var customImports = workspace.Content.Tfrags.CustomImports
            .Where(value => value.Pieces.Any(retainedIds.Contains)).Select(value => value with
            {
                Pieces = value.Pieces.Where(retainedIds.Contains).ToArray(),
            }).ToArray();
        var targetSources = workspace.Content.Tfrags.Sources.ToDictionary(
            value => (value.SourceKind, value.SourceIndex));
        var primaryTarget = targetSources.GetValueOrDefault((ProjectTfragSourceKind.Primary, 0))
            ?? throw new InvalidDataException("Target has no primary tfrag owner.");
        var sourceAttachments = source.Tfrags.Assets.ToDictionary(
            value => AssetId.Compute(value.Kind, value.CanonicalFormatVersion, value.CanonicalBytes.Span));
        var textureById = source.TerrainTextures.ToDictionary(value => value.SourceTextureId);
        var existingAssets = workspace.Content.Assets.Select(value => value.Id).ToHashSet();
        var imported = new List<ProjectEntity>();

        foreach (var donorSource in source.Tfrags.State.Sources
                     .OrderBy(value => value.SourceKind).ThenBy(value => value.SourceIndex))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = (donorSource.SourceKind, donorSource.SourceIndex);
            var donorPieces = source.Tfrags.Entities.Where(value => value.Tfrag is { } tfrag
                    && tfrag.SourceKind == donorSource.SourceKind && tfrag.SourceIndex == donorSource.SourceIndex)
                .OrderBy(value => value.Tfrag!.GroupIndex).ToArray();
            if (donorPieces.Length != donorSource.GroupCount)
                throw new InvalidDataException($"Donor tfrag owner {donorSource.SourceIndex} is incomplete.");
            if (donorPieces.Length == 0) continue;
            if (!targetSources.TryGetValue(key, out var targetSource))
            {
                if (donorSource.SourceKind != ProjectTfragSourceKind.Chunk)
                    throw new InvalidDataException(
                        $"Target has no matching {donorSource.SourceKind.ToString().ToLowerInvariant()} tfrag owner {donorSource.SourceIndex}.");
                targetSource = primaryTarget;
            }
            var payload = source.BaseLayers.SingleOrDefault(value => value.Layer == BakeLayerId.Tfrags
                && value.SourceIndex == donorSource.SourceIndex)
                ?? throw new InvalidDataException($"Donor tfrag owner {donorSource.SourceIndex} has no native payload.");
            var nativeAttachment = new ProjectAssetAttachment(
                AssetKind.Tfrag, NativeOwnerFormatVersion, payload.Bytes);
            var nativeId = AssetId.Compute(nativeAttachment.Kind, nativeAttachment.CanonicalFormatVersion,
                nativeAttachment.CanonicalBytes.Span);
            attachments.TryAdd(nativeId, nativeAttachment);
            var ownerId = Hash($"{plan.BatchId}\nowner\n{(byte)donorSource.SourceKind}\n{donorSource.SourceIndex}\n{nativeId}");
            var nextGroup = retained.Where(value => value.Tfrag is { } tfrag
                    && tfrag.SourceKind == targetSource.SourceKind
                    && tfrag.SourceIndex == targetSource.SourceIndex)
                .Select(value => value.Tfrag!.GroupIndex).DefaultIfEmpty(-1).Max() + 1;
            var pieceIds = new List<EntityId>(donorPieces.Length);
            var ownerImported = new List<ProjectEntity>(donorPieces.Length);
            foreach (var donor in donorPieces)
            {
                var canonical = sourceAttachments[donor.Asset!.Id];
                attachments.TryAdd(donor.Asset.Id, canonical);
                var groupIndex = nextGroup++;
                var entityId = new EntityId(StableGuid($"{ownerId}\npiece\n{donor.Tfrag!.GroupIndex}\n{donor.Asset.Id}"));
                pieceIds.Add(entityId);
                var occurrenceKey = Hash($"{ownerId}\noccurrence\n{donor.Tfrag.GroupIndex}");
                var importedPiece = donor with
                {
                    EntityId = entityId,
                    Name = $"Imported {donor.Name}",
                    Provenance = new("UYA", plan.SourceLevel, targetSource.SourceArchive, groupIndex),
                    State = null,
                    Tfrag = donor.Tfrag with
                    {
                        OccurrenceKey = occurrenceKey,
                        SourceArchive = targetSource.SourceArchive,
                        SourceKind = targetSource.SourceKind,
                        SourceIndex = targetSource.SourceIndex,
                        GroupIndex = groupIndex,
                        ImportedOwnerId = ownerId,
                    },
                };
                imported.Add(importedPiece);
                ownerImported.Add(importedPiece);
            }
            var usedTextures = donorPieces.SelectMany(value => value.Tfrag!.TextureIds).Distinct().Order().ToArray();
            var textures = usedTextures.Select(sourceTextureId =>
            {
                if (!textureById.TryGetValue(sourceTextureId, out var texture))
                    throw new InvalidDataException($"Donor tfrag texture {sourceTextureId} is missing.");
                var attachment = new ProjectAssetAttachment(
                    AssetKind.Texture, ProjectTextureAssetSchema.CanonicalFormatVersion, texture.PifBytes);
                var id = AssetId.Compute(attachment.Kind, attachment.CanonicalFormatVersion,
                    attachment.CanonicalBytes.Span);
                attachments.TryAdd(id, attachment);
                return new ProjectImportedTfragTexture(sourceTextureId, new(id, AssetKind.Texture));
            }).ToArray();
            importedOwners.Add(new(ownerId, plan.BatchId, plan.SourceLevel,
                donorSource.SourceArchive, donorSource.SourceKind, donorSource.SourceIndex,
                targetSource.SourceKind, targetSource.SourceIndex,
                new(nativeId, AssetKind.Tfrag), textures, pieceIds));
            retained.AddRange(ownerImported);
        }

        var entities = retained.ToArray();
        var sources = workspace.Content.Tfrags.Sources.Select(value => value with
        {
            GroupCount = entities.Count(entity => entity.Tfrag is { } tfrag
                && tfrag.SourceKind == value.SourceKind && tfrag.SourceIndex == value.SourceIndex),
        }).ToArray();
        var tfrags = workspace.Content.Tfrags with
        {
            Sources = sources,
            CustomImports = customImports,
            ImportedOwners = importedOwners.OrderBy(value => value.OwnerId, StringComparer.Ordinal).ToArray(),
        };
        var content = workspace.Content with
        {
            Entities = entities,
            Groups = workspace.Content.Groups.Select(value => value with
            {
                Members = value.Members.Where(retainedIds.Contains).ToArray(),
            }).ToArray(),
            Tfrags = tfrags,
        };
        var newAttachments = attachments.Where(value => !existingAssets.Contains(value.Key)).ToArray();
        var added = imported.Select(value => $"entity:{value.EntityId}")
            .Concat(importedOwners.Where(value => value.BatchId == plan.BatchId).Select(value => $"owner:{value.OwnerId}"))
            .Concat(newAttachments.Select(value => $"asset:{value.Key}"))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var batch = new ContentImportBatch(
            ContentImportBatchSchema.CurrentVersion, plan.BatchId, plan.SchemaVersion,
            plan.CapabilitySchemaVersion, importedUnixMilliseconds ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            plan.Game, plan.Region, plan.Revision, plan.SourceFingerprint, plan.SourceLevelHash, plan.SourceLevel,
            [new("tfrags", family.Mode)], added,
            attachments.Keys.Where(existingAssets.Contains)
                .Select(value => $"asset:{value}").Order(StringComparer.Ordinal).ToArray(),
            removed.Select(value => $"entity:{value}").Order(StringComparer.Ordinal).ToArray(),
            family.Protect.Order(StringComparer.Ordinal).ToArray());
        return new(plan.TargetFingerprint, batch, content, newAttachments.Select(value => value.Value).ToArray());
    }

    private static HashSet<EntityId> RemovalSet(
        ForgeProjectWorkspace workspace,
        ContentImportFamilyPlan family)
    {
        var result = new HashSet<EntityId>();
        if (family.Mode != ContentImportMode.Replace) return result;
        var tfrags = workspace.Content.Entities.Where(value => value.Tfrag is not null)
            .Select(value => value.EntityId).ToHashSet();
        foreach (var value in family.Remove)
        {
            if (!value.StartsWith("entity:", StringComparison.Ordinal))
                throw new InvalidDataException("Tfrag replacement contains an invalid target ID.");
            var id = EntityId.Parse(value["entity:".Length..]);
            if (!tfrags.Contains(id))
                throw new InvalidDataException("Tfrag replacement references a missing piece.");
            result.Add(id);
        }
        foreach (var owner in workspace.Content.Entities.Where(value => value.Tfrag is not null).GroupBy(value =>
                     (value.Tfrag!.SourceKind, value.Tfrag.SourceIndex)))
            if (owner.Any(value => result.Contains(value.EntityId)) && owner.Any(value => !result.Contains(value.EntityId)))
                throw new InvalidDataException("Tfrag replacement must retain or remove a complete target owner.");
        return result;
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static Guid StableGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value))[..16];
        bytes[7] = (byte)((bytes[7] & 0x0f) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new(bytes);
    }
}
