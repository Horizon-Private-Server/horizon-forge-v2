using Forge.Host.Domain;
using RatchetPs2.Games.UYA.Gameplay;

namespace Forge.Host.Games.UYA;

internal static class UyaMapGroupContentImportService
{
    private static readonly (ProjectEntityKind Kind, string Prefix, string Section, string MemberSection)[] Tables =
    [
        (ProjectEntityKind.Moby, "moby", "gameplay/moby_groups", "gameplay/core/moby_instances"),
        (ProjectEntityKind.Tie, "tie", "gameplay/tie_groups", "gameplay/core/tie_instances"),
        (ProjectEntityKind.Shrub, "shrub", "gameplay/shrub_groups", "gameplay/core/shrub_instances"),
    ];

    internal static ContentImportBatchCommit PrepareCommit(
        ForgeProjectWorkspace workspace,
        IReadOnlyList<ProjectEntity> donorEntities,
        IReadOnlyList<OpaqueSectionCapture> sections,
        ContentImportPlan plan,
        long? importedUnixMilliseconds = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(donorEntities);
        ArgumentNullException.ThrowIfNull(sections);
        ArgumentNullException.ThrowIfNull(plan);
        var family = UyaContentImportValidation.RequireSingleFamily(
            workspace, plan, "mapGroups", ContentImportMode.Replace,
            "This importer accepts one replace-only map-group family.");
        if (!family.Remove.SequenceEqual(["mapGroups:project"], StringComparer.Ordinal))
            throw new InvalidOperationException("This importer accepts one replace-only map-group family.");

        var tables = new List<ProjectMapGroupTable>();
        foreach (var (kind, prefix, sectionName, memberSection) in Tables)
        {
            if (!family.Add.Contains($"source:mapGroups:{prefix}:table")) continue;
            var matching = sections.Where(value => value.Name == sectionName).ToArray();
            if (matching.Length != 1)
                throw new InvalidDataException($"Donor {prefix} map groups require exactly one native section.");
            var members = donorEntities.Where(value => value.Provenance?.Section == memberSection)
                .ToDictionary(value => value.Provenance!.SourceIndex);
            var groups = UyaTieGroupsReader.Read(matching[0].Bytes).Groups.Select((group, groupIndex) =>
                new ProjectMapGroup(group.Select(sourceIndex =>
                {
                    if (!members.ContainsKey(sourceIndex))
                        throw new InvalidDataException(
                            $"Donor {prefix} map group {groupIndex} references missing instance {sourceIndex}.");
                    var matches = workspace.Content.Entities.Where(value =>
                            value.Provenance?.Level == plan.SourceLevel
                            && value.Provenance.Section == memberSection
                            && value.Provenance.SourceIndex == sourceIndex)
                        .Select(value => value.EntityId).Distinct().ToArray();
                    return matches.Length == 1
                        ? matches[0]
                        : throw new InvalidDataException(
                            $"Donor {prefix} instance {sourceIndex} does not map to one imported target entity.");
                }).ToArray())).ToArray();
            tables.Add(new(kind, groups));
        }

        var content = workspace.Content with { MapGroups = tables };
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
            [new("mapGroups", ContentImportMode.Replace)],
            [],
            [],
            ["mapGroups:project"],
            []);
        return new(plan.TargetFingerprint, batch, content, []);
    }
}
