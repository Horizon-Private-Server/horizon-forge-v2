namespace Forge.Host.Domain;

public sealed partial class EditorRuntime
{
    private EditorReferenceSnapshot[] ReferenceSnapshots(ForgeProjectWorkspace workspace)
    {
        var entityIds = workspace.Content.Entities.Select(entity => entity.EntityId).ToHashSet();
        var graph = ProjectReferenceGraph.Create(workspace.Content);
        return workspace.Content.Entities
            .SelectMany(entity => graph.Outgoing(entity.EntityId))
            .Select(edge => new EditorReferenceSnapshot(
                edge.OwnerEntityId,
                edge.Reference,
                edge.SourceValue,
                edge.Reference switch
                {
                    { Domain: ProjectReferenceDomain.Entity, EntityId: { } target } =>
                        !entityIds.Contains(target),
                    { Domain: ProjectReferenceDomain.Entity } => true,
                    { Domain: ProjectReferenceDomain.Asset, AssetId: { } target } =>
                        _missingAssets.Contains(target),
                    _ => true,
                }))
            .ToArray();
    }
}
