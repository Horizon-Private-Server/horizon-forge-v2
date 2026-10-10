namespace Forge.Host.Domain;

internal static class ProjectMobyPVars
{
    public static ForgeProjectContent Migrate(ForgeProjectContent content)
    {
        if (content.MobyPVars is not { SchemaVersion: 1 } state) return content;
        return content with
        {
            MobyPVars = new(
                ProjectMobyPVarSchema.CurrentVersion,
                state.SourceTableEntryCount,
                state.Entries.Select(value => value with
                {
                    Data = value.Data.ToArray(),
                    BaselineData = value.Data.ToArray(),
                }).ToArray()),
        };
    }

    public static IEnumerable<ProjectReferenceEdge> References(ProjectMobyPVarState? state) =>
        state?.Entries.SelectMany(pvar => pvar.References.Select(reference =>
            new ProjectReferenceEdge(
                pvar.EntityId,
                reference.Reference,
                reference.SourceValue,
                reference.Reference.EntityId is null && reference.SourceValue == reference.NullValue))) ?? [];

    public static ProjectMobyPVarState? RemoveAndClear(
        ProjectMobyPVarState? state,
        IReadOnlySet<EntityId> removed)
    {
        if (state is null) return null;
        var entries = state.Entries.Where(value => !removed.Contains(value.EntityId)).Select(value => value with
        {
            References = value.References.Select(reference =>
                reference.Reference.EntityId is { } target && removed.Contains(target)
                    ? reference with { Reference = reference.Reference with { EntityId = null } }
                    : reference).ToArray(),
        }).ToArray();
        return state with { Entries = entries };
    }

    public static ProjectMobyPVarState? UpdateReference(
        ProjectMobyPVarState? state,
        EntityId ownerEntityId,
        string fieldKey,
        int? sourceValue,
        EntityId? targetEntityId)
    {
        if (state is null) return null;
        var entryIndex = state.Entries.ToList().FindIndex(value => value.EntityId == ownerEntityId);
        if (entryIndex < 0) return state;
        var entries = state.Entries.ToArray();
        var references = entries[entryIndex].References.ToArray();
        var referenceIndex = references.ToList().FindIndex(value =>
            value.Reference.FieldKey == fieldKey && value.SourceValue == sourceValue);
        if (referenceIndex < 0) return state;
        if (targetEntityId is null && !references[referenceIndex].Reference.Nullable)
            throw new InvalidOperationException($"Entity field {fieldKey} is required.");
        references[referenceIndex] = references[referenceIndex] with
        {
            Reference = references[referenceIndex].Reference with { EntityId = targetEntityId },
        };
        entries[entryIndex] = entries[entryIndex] with { References = references };
        return state with { Entries = entries };
    }

    public static ProjectMobyPVarState? AddCopies(
        ProjectMobyPVarState? state,
        IReadOnlyList<ProjectEntity> originals,
        IReadOnlyList<ProjectEntity> copies)
    {
        if (state is null) return null;
        var byEntity = state.Entries.ToDictionary(value => value.EntityId);
        var additions = originals.Select((entity, index) => (entity, index))
            .Where(value => byEntity.ContainsKey(value.entity.EntityId))
            .Select(value =>
            {
                var source = byEntity[value.entity.EntityId];
                return source with
                {
                    EntityId = copies[value.index].EntityId,
                    SourceTableIndex = null,
                    Data = source.Data.ToArray(),
                    BaselineData = source.BaselineData.ToArray(),
                    MobyLinkOffsets = source.MobyLinkOffsets.ToArray(),
                    RelativePointerOffsets = source.RelativePointerOffsets.ToArray(),
                    References = source.References.Select(reference => reference with
                    {
                        Reference = reference.Reference with { },
                    }).ToArray(),
                };
            });
        return state with { Entries = state.Entries.Concat(additions).ToArray() };
    }
}
