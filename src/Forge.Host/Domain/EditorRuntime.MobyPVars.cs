namespace Forge.Host.Domain;

public sealed partial class EditorRuntime
{
    private static EditorMobyPVarResolutionIndex CreateMobyPVarResolutionIndex(ForgeProjectContent content)
    {
        var pvars = content.MobyPVars?.Entries.ToDictionary(value => value.EntityId)
            ?? new Dictionary<EntityId, ProjectMobyPVar>();
        var sourceEntities = content.Entities
            .Select(entity => (Entity: entity, SourceIndex: entity.Provenance?.SourceIndex ?? entity.Source?.SourceIndex))
            .Where(value => value.SourceIndex is not null)
            .GroupBy(value => (ProjectReferences.KindOf(value.Entity), value.SourceIndex!.Value))
            .ToDictionary(
                group => group.Key,
                group => group.Take(2).Count() == 1 ? (EntityId?)group.First().Entity.EntityId : null);
        return new(pvars, sourceEntities);
    }
}
