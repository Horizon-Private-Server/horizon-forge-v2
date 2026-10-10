namespace Forge.Host.Domain;

public sealed partial class EditorRuntime
{
    private static IEnumerable<EditorDiagnostic> GroupDiagnostics(IReadOnlyList<EditorGroupSnapshot> groups) =>
        groups.SelectMany(group => group.MissingMembers.Select(member => new EditorDiagnostic(
            "group.missing-member",
            EditorDiagnosticSeverity.Warning,
            $"Group {group.Name} contains missing entity {member}.")));

    private IReadOnlyList<EditorMapGroupSnapshot> MapGroupSnapshots(ForgeProjectWorkspace workspace)
    {
        var entities = workspace.Content.Entities.Where(entity => entity.State?.Disabled != true).ToArray();
        var imported = entities.Where(entity => entity.Provenance is not null)
            .GroupBy(entity => (entity.Provenance!.Section, entity.Provenance.SourceIndex))
            .ToDictionary(group => group.Key, group => group.Select(entity => entity.EntityId).ToArray());
        var projectCreated = entities.Where(entity => entity.Provenance is null && entity.Source?.SourceIndex is not null)
            .GroupBy(entity => (ProjectReferences.KindOf(entity), entity.Source!.SourceIndex!.Value))
            .ToDictionary(group => group.Key, group => group.Select(entity => entity.EntityId).ToArray());
        return _mapGroupSources.Select(group =>
        {
            var members = new List<EntityId>();
            var missing = new List<int>();
            var kind = ProjectKind(group.Kind);
            foreach (var sourceIndex in group.MemberSourceIndices)
            {
                var found = false;
                if (imported.TryGetValue((group.MemberSection, sourceIndex), out var importedMatches))
                {
                    members.AddRange(importedMatches);
                    found = true;
                }
                if (projectCreated.TryGetValue((kind, sourceIndex), out var projectMatches))
                {
                    members.AddRange(projectMatches);
                    found = true;
                }
                if (!found) missing.Add(sourceIndex);
            }
            return new EditorMapGroupSnapshot(group.Kind, group.SourceIndex, members, missing);
        }).ToArray();
    }

    private static ProjectEntityKind ProjectKind(EditorMapGroupKind kind) => kind switch
    {
        EditorMapGroupKind.Moby => ProjectEntityKind.Moby,
        EditorMapGroupKind.Tie => ProjectEntityKind.Tie,
        EditorMapGroupKind.Shrub => ProjectEntityKind.Shrub,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
