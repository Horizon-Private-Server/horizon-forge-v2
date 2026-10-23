using System.Security.Cryptography;
using System.Text;
using Forge.Host.Domain;
using RatchetPs2.Games.UYA.Gameplay;

namespace Forge.Host.Games.UYA;

public sealed record UyaStaticContentImportRequest(
    string ProjectPath,
    string CatalogRootPath,
    string SourceIsoPath,
    string SourceFingerprint,
    int SourceLevel,
    string BatchId,
    IReadOnlyList<ContentImportSelection> Selections);

public sealed record UyaStaticContentImportResult(
    string BatchId,
    int AddedEntityCount,
    int RemovedEntityCount,
    int ProtectedEntityCount,
    int AttachedAssetCount);

internal sealed record UyaStaticContentImportSource(
    IReadOnlyList<ProjectEntity> Entities,
    IReadOnlyList<OpaqueSectionCapture> OpaqueSections);

internal static class UyaStaticContentImportService
{
    private const long MaximumAssetBytes = ProjectTextureAssetSchema.MaximumCanonicalBytes;

    public static async Task<UyaStaticContentImportResult> ImportAsync(
        EditorRuntime runtime,
        UyaStaticContentImportRequest request,
        Func<IsoProgress, ValueTask>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ValidateSelections(request);
        var context = await UyaContentImportPlanningService.PrepareAsync(new(
            request.ProjectPath,
            request.CatalogRootPath,
            request.SourceIsoPath,
            request.SourceFingerprint,
            request.SourceLevel,
            request.Selections), progress, cancellationToken);
        if (!string.Equals(context.Plan.BatchId, request.BatchId, StringComparison.Ordinal))
            throw new InvalidOperationException("The import plan changed; review the refreshed plan before importing.");
        var command = await PrepareCommitAsync(
            context.Workspace,
            context.Catalog,
            new(context.Source.Entities, context.Source.OpaqueSections),
            context.Plan,
            null,
            cancellationToken);
        await runtime.CommitContentImportAsync(command, cancellationToken);
        var selected = context.Plan.Families.Where(value => value.Selected).ToArray();
        return new(
            command.Batch.BatchId,
            command.Batch.Added.Count(value => value.StartsWith("entity:", StringComparison.Ordinal)),
            command.Batch.Replaced.Count,
            selected.Sum(value => value.Protect.Count),
            command.Attachments.Count);
    }

    internal static async Task<ContentImportBatchCommit> PrepareCommitAsync(
        ForgeProjectWorkspace workspace,
        AssetCatalogStore catalog,
        UyaStaticContentImportSource source,
        ContentImportPlan plan,
        long? importedUnixMilliseconds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(plan);
        UyaContentImportValidation.ValidatePlan(workspace, plan);
        var selected = plan.Families.Where(value => value.Selected).OrderBy(value => value.Family).ToArray();
        if (selected.Length == 0 || selected.Any(value => value.Family is not ("ties" or "shrubs")))
            throw new InvalidOperationException("This importer accepts selected TIE and shrub families only.");

        var removed = RemovalSet(workspace, selected);
        var retained = workspace.Content.Entities.Where(value => !removed.Contains(value.EntityId)).ToArray();
        var imported = new List<ProjectEntity>();
        var importedGroups = new List<ProjectGroup>();
        var attachments = new Dictionary<AssetId, ProjectAssetAttachment>();
        var donorAssets = new HashSet<AssetId>();
        var existingAssets = workspace.Content.Assets.Select(value => value.Id).ToHashSet();
        var registry = workspace.Content.CustomClasses;

        foreach (var family in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var kind = Kind(family.Family);
            var donor = DonorEntities(source, family.Family, kind);
            var retainedClasses = StaticClasses(retained, family.Family, kind);
            var rebase = UyaCustomTieClassRegistry.RebaseStaticDonor(
                registry, kind, StaticClasses(donor, family.Family, kind), retainedClasses);
            registry = rebase.Registry;
            var remaps = rebase.Remaps.ToDictionary(
                value => (value.SourceClassId, value.AssetId), value => value.TargetClassId);
            var bySourceIndex = new Dictionary<int, EntityId>();
            foreach (var entity in donor.OrderBy(value => value.Provenance!.SourceIndex))
            {
                var asset = entity.Asset!;
                donorAssets.Add(asset.Id);
                var sourceRecord = entity.Source!;
                var targetId = ImportedEntityId(plan.BatchId, family.Family, entity);
                var targetClass = remaps[(sourceRecord.ClassId, asset.Id)];
                bySourceIndex.Add(entity.Provenance!.SourceIndex, targetId);
                imported.Add(entity with
                {
                    EntityId = targetId,
                    Name = $"Imported {entity.Name}",
                    State = null,
                    Source = sourceRecord with { ClassId = targetClass },
                });
                if (!existingAssets.Contains(asset.Id)
                    && !attachments.ContainsKey(asset.Id))
                    attachments.Add(asset.Id, await ReadAttachmentAsync(
                        catalog, asset, plan.SourceLevel, plan.SourceFingerprint, cancellationToken));
            }
            importedGroups.AddRange(ImportGroups(source, plan, family.Family, bySourceIndex));
        }

        var entities = retained.Concat(imported).Select(value =>
            ProjectReferences.ClearNullableEntityReferences(value, removed)).ToArray();
        var usedAssets = entities.Select(value => value.Asset).OfType<ProjectAssetReference>().ToHashSet();
        registry = registry with
        {
            Allocations = registry.Allocations.Where(value =>
                    value.Provenance.Origin != ProjectCustomClassOrigin.DonorMap || usedAssets.Contains(value.Asset))
                .OrderBy(value => value.Asset.Kind).ThenBy(value => value.ClassId)
                .ThenBy(value => value.Asset.Id.ToString(), StringComparer.Ordinal).ToArray(),
        };
        var importedGroupIds = ImportedGroupIds(workspace.Content);
        var groups = CleanGroups(workspace.Content.Groups, removed)
            .Where(value => value.Members.Count > 0 || !importedGroupIds.Contains(value.GroupId))
            .Concat(importedGroups).ToArray();
        var content = workspace.Content with
        {
            Entities = entities,
            LevelSettings = ProjectReferences.ClearNullableEntityReferences(workspace.Content.LevelSettings, removed),
            InstancedCollisionBindings = workspace.Content.InstancedCollisionBindings
                .Where(value => value.InstanceEntityId is null || !removed.Contains(value.InstanceEntityId.Value))
                .ToArray(),
            MobyPVars = ProjectMobyPVars.RemoveAndClear(workspace.Content.MobyPVars, removed),
            Groups = groups,
            CustomClasses = registry,
        };
        var added = imported.Select(value => $"entity:{value.EntityId}")
            .Concat(importedGroups.Select(value => $"group:{value.GroupId}"))
            .Concat(attachments.Keys.Select(value => $"asset:{value}"))
            .Concat(registry.Allocations.Except(workspace.Content.CustomClasses.Allocations)
                .Select(value => $"class:{value.Asset.Kind.ToString().ToLowerInvariant()}:{value.ClassId}"))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
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
            selected.Select(value => new ContentImportBatchFamily(value.Family, value.Mode)).ToArray(),
            added,
            selected.SelectMany(value => value.Reuse)
                .Concat(donorAssets.Where(existingAssets.Contains)
                    .Select(id => $"asset:{id}"))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            removed.Select(value => $"entity:{value}").Order(StringComparer.Ordinal).ToArray(),
            selected.SelectMany(value => value.Protect).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        return new(plan.TargetFingerprint, batch, content, attachments.Values
            .OrderBy(value => AssetId.Compute(value.Kind, value.CanonicalFormatVersion, value.CanonicalBytes.Span).ToString(),
                StringComparer.Ordinal).ToArray());
    }

    private static HashSet<EntityId> RemovalSet(
        ForgeProjectWorkspace workspace,
        IReadOnlyList<ContentImportFamilyPlan> selected)
    {
        var result = new HashSet<EntityId>();
        var plannedCollision = new HashSet<EntityId>();
        var entities = workspace.Content.Entities.ToDictionary(value => value.EntityId);
        foreach (var plan in selected.Where(value => value.Mode == ContentImportMode.Replace))
            foreach (var stableId in plan.Remove)
            {
                if (!stableId.StartsWith("entity:", StringComparison.Ordinal))
                    throw new InvalidDataException($"{plan.Family} replacement contains an invalid target ID.");
                var id = EntityId.Parse(stableId["entity:".Length..]);
                if (!entities.TryGetValue(id, out var entity))
                    throw new InvalidOperationException("The import plan references a missing target entity.");
                if (string.Equals(entity.Layer, plan.Family, StringComparison.Ordinal))
                    result.Add(id);
                else if (entity.Collision is not null)
                    plannedCollision.Add(id);
                else
                    throw new InvalidDataException("The import plan target family does not match the entity.");
            }
        var ownedCollision = entities.Values.Where(value =>
                     value.Collision?.LinkedInstanceEntityIds is { Count: > 0 } links
                     && links.All(result.Contains))
            .Select(value => value.EntityId).ToHashSet();
        if (!plannedCollision.IsSubsetOf(ownedCollision))
            throw new InvalidDataException("The import plan contains collision not owned by a replaced static instance.");
        result.UnionWith(ownedCollision);
        return result;
    }

    private static ProjectEntity[] DonorEntities(UyaStaticContentImportSource source, string family, AssetKind kind)
    {
        var result = source.Entities.Where(value => value.Layer == family).ToArray();
        if (result.Any(value => value.Asset is null || value.Asset.Kind != kind || value.Source is null
                || value.Provenance is null || value.Provenance.SourceIndex < 0)
            || result.Select(value => value.Provenance!.SourceIndex).Distinct().Count() != result.Length)
            throw new InvalidDataException($"Donor {family} are missing a unique instance, class, or asset identity.");
        return result;
    }

    private static UyaDonorStaticClass[] StaticClasses(
        IEnumerable<ProjectEntity> entities,
        string family,
        AssetKind kind) => entities.Where(value => value.Layer == family)
        .Select(value => value.Asset is { Kind: var assetKind } asset && assetKind == kind && value.Source is { } source
            ? new UyaDonorStaticClass(source.ClassId, asset.Id)
            : throw new InvalidDataException($"{family} entity is missing a compatible class or asset identity."))
        .Distinct().ToArray();

    private static async Task<ProjectAssetAttachment> ReadAttachmentAsync(
        AssetCatalogStore catalog,
        ProjectAssetReference asset,
        int sourceLevel,
        string sourceFingerprint,
        CancellationToken cancellationToken)
    {
        var entry = catalog.Query(new(Id: asset.Id)).SingleOrDefault()
            ?? throw new FileNotFoundException($"Donor asset {asset.Id} is missing from the catalog.");
        var level = $"level{sourceLevel:00}";
        if (entry.Kind != asset.Kind
            || entry.CanonicalFormatVersion != UyaStaticLayerSchema.CanonicalFormatVersion
            || !entry.Sources.Any(value => value.Game == "UYA" && value.Region == "NTSC-U"
                && value.Revision == "1.00" && value.Level == level
                && value.Fingerprint.Equals(sourceFingerprint, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"Donor asset {asset.Id} has no matching {level} source identity.");
        var path = catalog.ResolveBlobPath(asset.Id)
            ?? throw new FileNotFoundException($"Donor asset blob {asset.Id} is missing.");
        var bytes = await AssetCatalogBlobReader.ReadVerifiedAsync(
            entry, path, MaximumAssetBytes, cancellationToken);
        var canonical = UyaCanonicalAssetCodec.Decode(bytes);
        var definitionLength = asset.Kind == AssetKind.Tie ? 0x20 : 0x30;
        if (canonical.DefinitionBytes.Length != definitionLength)
            throw new InvalidDataException($"Donor asset {asset.Id} has an incompatible definition.");
        return new(asset.Kind, entry.CanonicalFormatVersion, bytes);
    }

    private static IReadOnlyList<ProjectGroup> ImportGroups(
        UyaStaticContentImportSource source,
        ContentImportPlan plan,
        string family,
        IReadOnlyDictionary<int, EntityId> imported)
    {
        var sectionName = family == "ties" ? "gameplay/tie_groups" : "gameplay/shrub_groups";
        var sections = source.OpaqueSections.Where(value => value.Name == sectionName).ToArray();
        if (sections.Length == 0) return [];
        if (sections.Length > 1) throw new InvalidDataException($"Donor {family} contain duplicate group sections.");
        var groups = UyaTieGroupsReader.Read(sections[0].Bytes).Groups;
        return groups.Select((members, index) => new ProjectGroup(
            new(StableGuid($"{plan.BatchId}\ngroup\n{family}\n{index}")),
            $"Imported level {plan.SourceLevel} {family} group {index + 1}",
            members.Select(sourceIndex => imported.TryGetValue(sourceIndex, out var id)
                    ? id
                    : throw new InvalidDataException(
                        $"Donor {family} group {index} references missing instance {sourceIndex}."))
                .ToArray())).ToArray();
    }

    private static IEnumerable<ProjectGroup> CleanGroups(
        IEnumerable<ProjectGroup> groups,
        IReadOnlySet<EntityId> removed) => groups.Select(group => group with
    {
        Members = group.Members.Where(member => !removed.Contains(member)).ToArray(),
        Template = group.Template is not { } template
            || template.SeedEntityId is { } seed && removed.Contains(seed)
                ? null
                : template with
                {
                    GeneratedMembers = template.GeneratedMembers
                        .Where(member => !removed.Contains(member)).ToArray(),
                },
    });

    private static HashSet<GroupId> ImportedGroupIds(ForgeProjectContent content) => content.ImportBatches
        .SelectMany(value => value.Added)
        .Where(value => value.StartsWith("group:", StringComparison.Ordinal))
        .Select(value => GroupId.Parse(value["group:".Length..]))
        .ToHashSet();

    private static Guid StableGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value))[..16];
        bytes[7] = (byte)((bytes[7] & 0x0f) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new(bytes);
    }

    internal static EntityId ImportedEntityId(string batchId, string family, ProjectEntity entity) =>
        new(StableGuid(
            $"{batchId}\nentity\n{family}\n{entity.Provenance!.SourceIndex}\n{entity.Asset!.Id}"));

    private static AssetKind Kind(string family) => family switch
    {
        "ties" => AssetKind.Tie,
        "shrubs" => AssetKind.Shrub,
        _ => throw new ArgumentOutOfRangeException(nameof(family)),
    };

    private static void ValidateSelections(UyaStaticContentImportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.BatchId);
        if (request.Selections.Count == 0
            || request.Selections.Any(value => value.Family is not ("ties" or "shrubs")
                || value.Mode is not (ContentImportMode.Add or ContentImportMode.Replace)))
            throw new ArgumentException("Static import requires explicit TIE or shrub selections.", nameof(request));
    }
}
