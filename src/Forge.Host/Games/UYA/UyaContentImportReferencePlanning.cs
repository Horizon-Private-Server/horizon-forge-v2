using Forge.Host.Domain;

namespace Forge.Host.Games.UYA;

internal static partial class UyaContentImportPlanningService
{
    internal static ContentImportPlan AddExternalReferenceRequirements(
        ContentImportPlan plan,
        IReadOnlyList<UyaMobyExternalReferenceRequirement> sourceRequirements,
        ForgeProjectContent target)
    {
        if (!plan.Families.Any(value => value is { Family: "mobys", Selected: true }))
            return plan;
        var removed = plan.Families.Where(value => value.Selected)
            .SelectMany(value => value.Remove)
            .Where(value => value.StartsWith("entity:", StringComparison.Ordinal))
            .Select(value => EntityId.Parse(value["entity:".Length..]))
            .ToHashSet();
        var retained = target.Entities.Where(value => !removed.Contains(value.EntityId)).ToArray();
        var candidates = CandidateEntityIds(retained);
        var requirements = sourceRequirements
            .Select(value => new ContentImportReferenceRequirement(
                "mobys",
                value.OwnerSourceIndex,
                value.FieldKey,
                value.SourceValue,
                value.TargetKind,
                value.Nullable,
                Candidates(candidates, value.TargetKind)))
            .ToArray();
        var unavailable = requirements.Where(value => !value.Nullable && value.CandidateEntityIds.Count == 0)
            .Select(value => $"Moby #{value.OwnerSourceIndex} field {value.FieldKey} requires a retained "
                + $"{value.TargetKind} target, but none are available.")
            .ToArray();
        if (unavailable.Length == 0) return plan with { ReferenceRequirements = requirements };
        var families = plan.Families.Select(value => value.Family == "mobys"
            ? value with { Blockers = value.Blockers.Concat(unavailable).Distinct(StringComparer.Ordinal).ToArray() }
            : value).ToArray();
        return plan with
        {
            Families = families,
            Blockers = families.Where(value => value.Selected).SelectMany(value => value.Blockers)
                .Distinct(StringComparer.Ordinal).Take(ContentImportPlanSchema.MaximumDiagnostics).ToArray(),
            ReferenceRequirements = requirements,
        };
    }

    internal static ContentImportPlan AddLevelSettingsReferenceRequirements(
        ContentImportPlan plan,
        ProjectLevelSettings? settings,
        IReadOnlyList<ProjectEntity> sourceEntities,
        ForgeProjectContent target)
    {
        if (!plan.Families.Any(value => value is { Family: "levelSettings", Selected: true })
            || settings?.Scene is not { } scene)
            return plan;
        var removed = plan.Families.Where(value => value.Selected)
            .SelectMany(value => value.Remove)
            .Where(value => value.StartsWith("entity:", StringComparison.Ordinal))
            .Select(value => EntityId.Parse(value["entity:".Length..]))
            .ToHashSet();
        var retained = target.Entities.Where(value => !removed.Contains(value.EntityId)).ToArray();
        var candidates = CandidateEntityIds(retained);
        var requirements = new[]
        {
            Requirement(scene.ShipPath, ProjectEntityKind.Spline, "splines"),
            Requirement(scene.ShipCameraCuboidStart, ProjectEntityKind.Cuboid, "cuboids"),
            Requirement(scene.ShipCameraCuboidEnd, ProjectEntityKind.Cuboid, "cuboids"),
        }.Where(value => value is not null).Select(value => value!).ToArray();
        return plan with
        {
            ReferenceRequirements = (plan.ReferenceRequirements ?? []).Concat(requirements).ToArray(),
        };

        ContentImportReferenceRequirement? Requirement(
            ProjectIndexedReference value,
            ProjectEntityKind kind,
            string family)
        {
            if (value.SourceValue < 0) return null;
            var source = value.Reference.EntityId is { } sourceId
                ? sourceEntities.SingleOrDefault(candidate => candidate.EntityId == sourceId)
                : null;
            if (source is not null
                && source.Provenance?.SourceIndex == value.SourceValue
                && ProjectReferences.KindOf(source) == kind
                && plan.Families.Any(candidate => candidate.Family == family && candidate.Selected))
                return null;
            return new(
                "levelSettings",
                -1,
                value.Reference.FieldKey,
                value.SourceValue,
                kind,
                true,
                Candidates(candidates, kind));
        }
    }

    internal static ContentImportPlan AddAreaReferenceRequirements(
        ContentImportPlan plan,
        IReadOnlyList<ProjectEntity> sourceEntities,
        ForgeProjectContent target)
    {
        if (!plan.Families.Any(value => value is { Family: "areas", Selected: true })) return plan;
        var selectedFamilies = plan.Families.Where(value => value.Selected)
            .Select(value => value.Family).ToHashSet(StringComparer.Ordinal);
        var removed = plan.Families.Where(value => value.Selected)
            .SelectMany(value => value.Remove)
            .Where(value => value.StartsWith("entity:", StringComparison.Ordinal))
            .Select(value => EntityId.Parse(value["entity:".Length..]))
            .ToHashSet();
        var retained = target.Entities.Where(value => !removed.Contains(value.EntityId)).ToArray();
        var candidates = CandidateEntityIds(retained);
        var sourceById = sourceEntities.ToDictionary(value => value.EntityId);
        var requirements = sourceEntities.Where(value => value.Geometry?.Area is not null)
            .OrderBy(value => value.Provenance!.SourceIndex)
            .SelectMany(area => AreaLinks(area).Where(link => !ImportedWithSelectedFamily(link.Link))
                .Select(link => new ContentImportReferenceRequirement(
                    "areas",
                    area.Provenance!.SourceIndex,
                    link.Link.Reference!.FieldKey,
                    link.Link.SourceIndex,
                    link.Kind,
                    true,
                    Candidates(candidates, link.Kind))))
            .ToArray();
        return plan with
        {
            ReferenceRequirements = (plan.ReferenceRequirements ?? []).Concat(requirements).ToArray(),
        };

        bool ImportedWithSelectedFamily(ProjectGeometryLink link)
        {
            if (link.Reference?.EntityId is not { } id || !sourceById.TryGetValue(id, out var source)) return false;
            var family = Family(source);
            return family is not null && selectedFamilies.Contains(family)
                && source.Provenance?.SourceIndex == link.SourceIndex;
        }
    }

    private static IEnumerable<(ProjectGeometryLink Link, ProjectEntityKind Kind)> AreaLinks(ProjectEntity entity)
    {
        var area = entity.Geometry?.Area
            ?? throw new InvalidDataException("Donor area geometry is missing.");
        foreach (var value in area.Splines) yield return (value, ProjectEntityKind.Spline);
        foreach (var value in area.Cuboids) yield return (value, ProjectEntityKind.Cuboid);
        foreach (var value in area.Spheres) yield return (value, ProjectEntityKind.Sphere);
        foreach (var value in area.Cylinders) yield return (value, ProjectEntityKind.Cylinder);
        foreach (var value in area.NegativeCuboids) yield return (value, ProjectEntityKind.Cuboid);
    }

    private static IReadOnlyDictionary<ProjectEntityKind, EntityId[]> CandidateEntityIds(
        IEnumerable<ProjectEntity> entities) => entities
        .GroupBy(ProjectReferences.KindOf)
        .ToDictionary(
            group => group.Key,
            group => group.Select(value => value.EntityId)
                .OrderBy(value => value.ToString(), StringComparer.Ordinal).ToArray());

    private static IReadOnlyList<EntityId> Candidates(
        IReadOnlyDictionary<ProjectEntityKind, EntityId[]> candidates,
        ProjectEntityKind kind) => candidates.TryGetValue(kind, out var values) ? values : [];

    internal static ContentImportPlan AddDirectionalLightBlockers(
        ContentImportPlan plan,
        IReadOnlyList<ProjectEntity> sourceEntities,
        ForgeProjectContent target)
    {
        if (!plan.Families.Any(value => value is { Family: "directionalLights", Selected: true }))
            return plan;
        var tiePlan = plan.Families.Single(value => value.Family == "ties");
        var removed = tiePlan.Selected && tiePlan.Mode == ContentImportMode.Replace
            ? tiePlan.Remove.Where(value => value.StartsWith("entity:", StringComparison.Ordinal))
                .Select(value => EntityId.Parse(value["entity:".Length..])).ToHashSet()
            : [];
        var mapGroupedTargetIds = target.MapGroups.SelectMany(value => value.Groups)
            .SelectMany(value => value.Members).ToHashSet();
        var finalTies = target.Entities
            .Where(value => value.Layer == "ties" && !removed.Contains(value.EntityId))
            .Select(value => (Entity: value, Retained: true, MapGrouped: mapGroupedTargetIds.Contains(value.EntityId)));
        if (tiePlan.Selected)
            finalTies = finalTies.Concat(sourceEntities.Where(value => value.Layer == "ties")
                .Select(value => (Entity: value, Retained: false, MapGrouped: false)));
        var lightCount = sourceEntities.Count(value => value.Lighting?.DirectionalLight is not null);
        var blockers = finalTies.Where(value => value.Entity.TieLighting is { } lighting
                && !UyaDirectionalLightsContentImportService.SelectorFits(lighting.DirectionalLights, lightCount))
            .GroupBy(value => (
                value.Entity.TieLighting!.DirectionalLights,
                value.Retained,
                value.MapGrouped))
            .OrderBy(value => value.Key.Retained ? 0 : 1)
            .ThenBy(value => unchecked((uint)value.Key.DirectionalLights))
            .Select(value =>
            {
                var origin = value.Key.Retained ? "retained target" : "donor";
                var examples = string.Join(", ", value.Take(3).Select(item => item.Entity.Name));
                var remedy = value.Key is { Retained: true, MapGrouped: true }
                    ? " Select Map groups → Replace so the old grouped TIEs can be removed, or set Directional lights to Skip."
                    : value.Key.Retained
                        ? " Remove or remap the retained TIEs, or set Directional lights to Skip."
                        : " The donor TIE lighting data is not compatible with its directional-light array.";
                return $"{value.Count()} {origin} TIE(s) use directional-light selector "
                    + $"0x{value.Key.DirectionalLights:X8}, which does not fit the donor's {lightCount}-light array. "
                    + $"Examples: {examples}.{remedy}";
            })
            .Take(ContentImportPlanSchema.MaximumDiagnostics)
            .ToArray();
        if (blockers.Length == 0) return plan;
        var families = plan.Families.Select(value => value.Family == "directionalLights"
            ? value with { Blockers = value.Blockers.Concat(blockers).Distinct(StringComparer.Ordinal).ToArray() }
            : value).ToArray();
        return plan with
        {
            Families = families,
            Blockers = families.Where(value => value.Selected).SelectMany(value => value.Blockers)
                .Distinct(StringComparer.Ordinal).Take(ContentImportPlanSchema.MaximumDiagnostics).ToArray(),
        };
    }

}
