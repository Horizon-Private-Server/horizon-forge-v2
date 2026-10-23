using System.Security.Cryptography;
using System.Text;
using Forge.Host.Domain;

namespace Forge.Host.Games.UYA;

public sealed record UyaGeometryContentImportRequest(
    string ProjectPath,
    string CatalogRootPath,
    string SourceIsoPath,
    string SourceFingerprint,
    int SourceLevel,
    string BatchId,
    IReadOnlyList<ContentImportSelection> Selections,
    IReadOnlyList<UyaMobyExternalReferenceChoice>? ReferenceChoices = null);

internal static class UyaGeometryContentImportService
{
    private static readonly string[] Families = ["cuboids", "spheres", "splines", "paths", "areas"];

    public static async Task<UyaStaticContentImportResult> ImportAsync(
        EditorRuntime runtime,
        UyaGeometryContentImportRequest request,
        Func<IsoProgress, ValueTask>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ValidateSelections(request.Selections);
        var context = await UyaContentImportPlanningService.PrepareAsync(new(
            request.ProjectPath, request.CatalogRootPath, request.SourceIsoPath,
            request.SourceFingerprint, request.SourceLevel, request.Selections), progress, cancellationToken);
        if (context.Plan.BatchId != request.BatchId)
            throw new InvalidOperationException("The import plan changed; review the refreshed plan before importing.");
        var command = PrepareCommit(
            context.Workspace, context.Source.Entities, context.Plan, cancellationToken: cancellationToken,
            referenceChoices: request.ReferenceChoices);
        await runtime.CommitContentImportAsync(command, cancellationToken);
        var selected = context.Plan.Families.Where(value => value.Selected).ToArray();
        return new(command.Batch.BatchId,
            command.Batch.Added.Count(value => value.StartsWith("entity:", StringComparison.Ordinal)),
            command.Batch.Replaced.Count, selected.Sum(value => value.Protect.Count), 0);
    }

    internal static ContentImportBatchCommit PrepareCommit(
        ForgeProjectWorkspace workspace,
        IReadOnlyList<ProjectEntity> donorEntities,
        ContentImportPlan plan,
        long? importedUnixMilliseconds = null,
        CancellationToken cancellationToken = default,
        IReadOnlyList<UyaMobyExternalReferenceChoice>? referenceChoices = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(donorEntities);
        ArgumentNullException.ThrowIfNull(plan);
        UyaContentImportValidation.ValidatePlan(workspace, plan);
        var selected = plan.Families.Where(value => value.Selected).OrderBy(value => value.Family).ToArray();
        if (selected.Length == 0 || selected.Any(value => !Families.Contains(value.Family)
                || value.Mode is not (ContentImportMode.Add or ContentImportMode.Replace)
                || !value.Selectable)
            || selected.Select(value => value.Family).Distinct(StringComparer.Ordinal).Count() != selected.Length)
            throw new InvalidOperationException("This importer accepts selected geometry and area families only.");

        var removed = RemovalSet(workspace, selected);
        var entities = workspace.Content.Entities.Where(value => !removed.Contains(value.EntityId)).ToList();
        var imported = new List<ProjectEntity>();
        foreach (var family in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var section = Section(family.Family);
            var donor = donorEntities.Where(value => Family(value) == family.Family)
                .OrderBy(value => value.Provenance!.SourceIndex).ToArray();
            if (donor.Any(value => value.Geometry is null || value.Asset is not null || value.Source is not null
                    || value.Provenance is not { Game: "UYA" } provenance || provenance.Section != section)
                || donor.Select(value => value.Provenance!.SourceIndex).Distinct().Count() != donor.Length)
                throw new InvalidDataException($"Donor {family.Family} inventory is incomplete or invalid.");
            foreach (var value in donor)
            {
                var id = ImportedEntityId(
                    plan.BatchId, family.Family, value.Provenance!.SourceIndex);
                imported.Add(value with
                {
                    EntityId = id,
                    Name = $"Imported {value.Name}",
                    State = null,
                    Provenance = value.Provenance with { Level = plan.SourceLevel },
                });
            }
        }
        entities.AddRange(imported);
        foreach (var family in selected)
        {
            var section = Section(family.Family);
            var index = 0;
            for (var entityIndex = 0; entityIndex < entities.Count; entityIndex++)
            {
                var entity = entities[entityIndex];
                if (Family(entity) != family.Family) continue;
                entities[entityIndex] = entity with
                {
                    Provenance = entity.Provenance! with { Section = section, SourceIndex = index++ },
                };
            }
        }

        RebindImportedAreas(entities, donorEntities, plan, selected, referenceChoices ?? []);

        var content = workspace.Content with { Entities = entities.ToArray() };
        var batch = new ContentImportBatch(
            ContentImportBatchSchema.CurrentVersion, plan.BatchId, plan.SchemaVersion,
            plan.CapabilitySchemaVersion, importedUnixMilliseconds ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            plan.Game, plan.Region, plan.Revision, plan.SourceFingerprint, plan.SourceLevelHash, plan.SourceLevel,
            selected.Select(value => new ContentImportBatchFamily(value.Family, value.Mode)).ToArray(),
            imported.Select(value => $"entity:{value.EntityId}").Order(StringComparer.Ordinal).ToArray(),
            [], removed.Select(value => $"entity:{value}").Order(StringComparer.Ordinal).ToArray(),
            selected.SelectMany(value => value.Protect).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToArray());
        return new(plan.TargetFingerprint, batch, content, []);
    }

    private static HashSet<EntityId> RemovalSet(
        ForgeProjectWorkspace workspace,
        IReadOnlyList<ContentImportFamilyPlan> selected)
    {
        var result = new HashSet<EntityId>();
        var entities = workspace.Content.Entities.ToDictionary(value => value.EntityId);
        foreach (var family in selected.Where(value => value.Mode == ContentImportMode.Replace))
            foreach (var stableId in family.Remove)
            {
                if (!stableId.StartsWith("entity:", StringComparison.Ordinal))
                    throw new InvalidDataException($"{family.Family} replacement contains an invalid target ID.");
                var id = EntityId.Parse(stableId["entity:".Length..]);
                if (!entities.TryGetValue(id, out var entity))
                    throw new InvalidOperationException("The import plan references a missing target entity.");
                if (Family(entity) != family.Family)
                    throw new InvalidDataException("The import plan target family does not match the entity.");
                result.Add(id);
            }
        return result;
    }

    private static string? Family(ProjectEntity entity) => entity.Geometry switch
    {
        { Cuboid: not null } => "cuboids",
        { Sphere: not null } => "spheres",
        { Spline: not null } => "splines",
        { GrindPath: not null } => "paths",
        { Area: not null } => "areas",
        _ => null,
    };

    private static string Section(string family) => family switch
    {
        "cuboids" => "gameplay/core/cuboids",
        "spheres" => "gameplay/core/spheres",
        "splines" => "gameplay/core/splines",
        "paths" => "gameplay/core/grind_splines",
        "areas" => "gameplay/core/areas",
        _ => throw new ArgumentOutOfRangeException(nameof(family)),
    };

    private static void ValidateSelections(IReadOnlyList<ContentImportSelection> selections)
    {
        ArgumentNullException.ThrowIfNull(selections);
        if (selections.Count == 0 || selections.Any(value => !Families.Contains(value.Family)
                || value.Mode is not (ContentImportMode.Add or ContentImportMode.Replace))
            || selections.Select(value => value.Family).Distinct(StringComparer.Ordinal).Count() != selections.Count)
            throw new ArgumentException("Geometry import requires explicit shape, spline, path, or area selections.",
                nameof(selections));
    }

    private static void RebindImportedAreas(
        IList<ProjectEntity> entities,
        IReadOnlyList<ProjectEntity> donorEntities,
        ContentImportPlan plan,
        IReadOnlyList<ContentImportFamilyPlan> selected,
        IReadOnlyList<UyaMobyExternalReferenceChoice> choices)
    {
        if (selected.All(value => value.Family != "areas"))
        {
            if (choices.Count > 0) throw new InvalidDataException("Area reference choices were supplied without areas.");
            return;
        }
        var requirements = (plan.ReferenceRequirements ?? []).Where(value => value.Family == "areas").ToArray();
        if (choices.Count != requirements.Length
            || choices.Select(Key).Distinct().Count() != choices.Count
            || choices.Any(choice => !requirements.Any(requirement => Key(requirement) == Key(choice))))
            throw new InvalidDataException("Area reference choices do not match the approved plan.");
        var choiceByKey = choices.ToDictionary(Key);
        var donorById = donorEntities.ToDictionary(value => value.EntityId);
        var finalById = entities.ToDictionary(value => value.EntityId);
        var selectedFamilies = selected.Select(value => value.Family).ToHashSet(StringComparer.Ordinal);
        foreach (var donor in donorEntities.Where(value => Family(value) == "areas"))
        {
            var importedId = ImportedEntityId(plan.BatchId, "areas", donor.Provenance!.SourceIndex);
            var index = entities.ToList().FindIndex(value => value.EntityId == importedId);
            if (index < 0) throw new InvalidDataException("An imported donor area is missing from the final inventory.");
            var entity = entities[index];
            var source = donor.Geometry!.Area!;
            entities[index] = entity with
            {
                Geometry = entity.Geometry! with
                {
                    Area = source with
                    {
                        Splines = Rebind(source.Splines),
                        Cuboids = Rebind(source.Cuboids),
                        Spheres = Rebind(source.Spheres),
                        Cylinders = Rebind(source.Cylinders),
                        NegativeCuboids = Rebind(source.NegativeCuboids),
                    },
                },
            };

            ProjectGeometryLink[] Rebind(IReadOnlyList<ProjectGeometryLink> links) => links
                .Select(RebindLink).OfType<ProjectGeometryLink>()
                .DistinctBy(value => value.SourceIndex).ToArray();

            ProjectGeometryLink? RebindLink(ProjectGeometryLink link)
            {
                var reference = link.Reference
                    ?? throw new InvalidDataException($"Donor area #{donor.Provenance!.SourceIndex} has an unmigrated link.");
                if (reference is not
                    { Domain: ProjectReferenceDomain.Entity, Nullable: true, EntityKind: var targetKind })
                    throw new InvalidDataException(
                        $"Donor area #{donor.Provenance!.SourceIndex} field {reference.FieldKey} is invalid.");
                var donorTarget = reference.EntityId is { } donorId
                    ? donorById.GetValueOrDefault(donorId)
                    : null;
                var importedFamily = donorTarget is null ? null : Family(donorTarget);
                if (importedFamily is not null && selectedFamilies.Contains(importedFamily))
                {
                    var importedId = ImportedEntityId(
                        plan.BatchId, importedFamily, donorTarget!.Provenance!.SourceIndex);
                    if (finalById.GetValueOrDefault(importedId) is { } imported
                        && ProjectReferences.KindOf(imported) == targetKind)
                        return Bind(link, reference, imported);
                }

                var key = (donor.Provenance!.SourceIndex, reference.FieldKey, link.SourceIndex);
                var requirement = requirements.SingleOrDefault(value => Key(value) == key)
                    ?? throw new InvalidDataException(
                        $"Donor area #{donor.Provenance.SourceIndex} field {reference.FieldKey} has no approved mapping.");
                var choice = choiceByKey[key];
                if (!Enum.IsDefined(choice.Action))
                    throw new InvalidDataException("An area reference choice has an invalid action.");
                if (choice.Action == UyaMobyExternalReferenceAction.Clear)
                {
                    if (choice.TargetEntityId is not null)
                        throw new InvalidDataException("An area clear choice cannot contain a target entity.");
                    return null;
                }
                if (choice.TargetEntityId is not { } targetId
                    || !requirement.CandidateEntityIds.Contains(targetId)
                    || !finalById.TryGetValue(targetId, out var target)
                    || ProjectReferences.KindOf(target) != targetKind)
                    throw new InvalidDataException(
                        $"Donor area #{donor.Provenance.SourceIndex} field {reference.FieldKey} maps to a missing or incompatible target.");
                return Bind(link, reference, target);
            }
        }
    }

    private static ProjectGeometryLink Bind(
        ProjectGeometryLink link,
        ProjectReference reference,
        ProjectEntity target) => link with
    {
        SourceIndex = target.Provenance?.SourceIndex
            ?? throw new InvalidDataException("An area reference target has no native source index."),
        Reference = reference with { EntityId = target.EntityId },
    };

    private static (int OwnerSourceIndex, string FieldKey, int SourceValue) Key(
        UyaMobyExternalReferenceChoice value) =>
        (value.OwnerSourceIndex, value.FieldKey, value.SourceValue);

    private static (int OwnerSourceIndex, string FieldKey, int SourceValue) Key(
        ContentImportReferenceRequirement value) =>
        (value.OwnerSourceIndex, value.FieldKey, value.SourceValue);

    internal static EntityId ImportedEntityId(string batchId, string family, int sourceIndex) =>
        new(StableGuid($"{batchId}\nentity\n{family}\n{sourceIndex}"));

    private static Guid StableGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value))[..16];
        bytes[7] = (byte)((bytes[7] & 0x0f) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new(bytes);
    }
}
