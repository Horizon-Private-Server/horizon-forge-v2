namespace Forge.Host.Domain;

public sealed partial class EditorRuntime
{
    private EditorReferenceSnapshot[] ReferenceSnapshots(
        ForgeProjectWorkspace workspace,
        IReadOnlyDictionary<EntityId, EditorEntitySnapshot> entities)
    {
        var graph = ProjectReferenceGraph.Create(workspace.Content);
        var datasetSources = ReferenceDatasetSources(entities.Values);
        return workspace.Content.Entities
            .SelectMany(entity => graph.Outgoing(entity.EntityId))
            .Select(edge => new EditorReferenceSnapshot(
                edge.OwnerEntityId,
                edge.Reference,
                edge.SourceValue,
                edge.Reference switch
                {
                    { Domain: ProjectReferenceDomain.Entity, EntityId: { } target } =>
                        !entities.ContainsKey(target),
                    { Domain: ProjectReferenceDomain.Entity } => !edge.IsNull,
                    { Domain: ProjectReferenceDomain.Asset, AssetId: { } target } =>
                        _missingAssets.Contains(target),
                    _ => true,
                },
                datasetSources.GetValueOrDefault((edge.OwnerEntityId, edge.Reference.FieldKey))))
            .ToArray();
    }

    private static IReadOnlyDictionary<(EntityId Owner, string FieldKey), string> ReferenceDatasetSources(
        IEnumerable<EditorEntitySnapshot> entities)
    {
        var result = new Dictionary<(EntityId, string), string>();
        foreach (var entity in entities)
        {
            if (entity.MobyPVar is not { } pvar) continue;
            var source = pvar.SchemaSource == "Project override"
                ? $"Project override · {pvar.DatasetId} v{pvar.DatasetVersion}"
                : pvar.SchemaSource;
            foreach (var fieldKey in ReferenceFieldKeys(pvar.Fields))
                result.Add((entity.EntityId, fieldKey), source);
        }
        return result;
    }

    private static IEnumerable<string> ReferenceFieldKeys(
        IReadOnlyList<EditorMobyPVarFieldDescriptor>? fields) => fields?.SelectMany(field =>
            (field.Kind == EditorMobyPVarFieldKind.Reference ? new[] { field.Path } : [])
                .Concat(ReferenceFieldKeys(field.Children))) ?? [];
}
