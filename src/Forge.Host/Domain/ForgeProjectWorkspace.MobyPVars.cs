namespace Forge.Host.Domain;

public sealed partial class ForgeProjectWorkspace
{
    public ProjectMobyPVar ReplaceMobyPVarData(EntityId entityId, ReadOnlySpan<byte> data)
    {
        var state = Content.MobyPVars
            ?? throw new KeyNotFoundException($"Entity {entityId} has no project PVar state.");
        var index = FindMobyPVarIndex(state, entityId);
        var previous = Content;
        var entries = state.Entries.ToArray();
        if (data.Length != entries[index].Data.Length)
            throw new InvalidDataException("A Moby PVar replacement cannot change its length.");
        entries[index] = entries[index] with { Data = data.ToArray() };
        Content = Content with { MobyPVars = state with { Entries = entries } };
        try { Validate(); }
        catch
        {
            Content = previous;
            throw;
        }
        return entries[index];
    }

    public ProjectMobyPVar ReplaceMobyPVar(ProjectMobyPVar replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        var state = Content.MobyPVars
            ?? throw new KeyNotFoundException($"Entity {replacement.EntityId} has no project PVar state.");
        var index = FindMobyPVarIndex(state, replacement.EntityId);
        if (replacement.Data.Length != state.Entries[index].Data.Length)
            throw new InvalidDataException("A Moby PVar replacement cannot change its length.");
        var previous = Content;
        var entries = state.Entries.ToArray();
        entries[index] = replacement with
        {
            Data = replacement.Data.ToArray(),
            BaselineData = replacement.BaselineData.ToArray(),
        };
        Content = Content with { MobyPVars = state with { Entries = entries } };
        try { Validate(); }
        catch
        {
            Content = previous;
            throw;
        }
        return entries[index];
    }

    public int MergeMissingMobyPVars(ProjectMobyPVarState imported)
    {
        ArgumentNullException.ThrowIfNull(imported);
        var previous = Content;
        var current = Content.MobyPVars;
        var sourceEntryCount = current is { SourceTableEntryCount: > 0 }
            ? current.SourceTableEntryCount
            : imported.SourceTableEntryCount;
        if (current is { SourceTableEntryCount: > 0 }
            && current.SourceTableEntryCount != imported.SourceTableEntryCount)
            throw new InvalidDataException("Project and retained source PVar table counts do not match.");
        var entries = current?.Entries.ToList() ?? [];
        var owners = entries.Select(value => value.EntityId).ToHashSet();
        var sourceIndexes = entries.Select(value => value.SourceTableIndex).OfType<int>().ToHashSet();
        foreach (var candidate in imported.Entries.Where(value => !owners.Contains(value.EntityId)))
        {
            int? sourceIndex = candidate.SourceTableIndex is { } index && sourceIndexes.Add(index) ? index : null;
            entries.Add(candidate with
            {
                SourceTableIndex = sourceIndex,
                Data = candidate.Data.ToArray(),
                BaselineData = candidate.BaselineData.ToArray(),
                MobyLinkOffsets = candidate.MobyLinkOffsets.ToArray(),
                RelativePointerOffsets = candidate.RelativePointerOffsets.ToArray(),
                References = candidate.References.ToArray(),
            });
            owners.Add(candidate.EntityId);
        }
        var added = entries.Count - (current?.Entries.Count ?? 0);
        if (added == 0) return 0;
        Content = Content with
        {
            MobyPVars = new(ProjectMobyPVarSchema.CurrentVersion, sourceEntryCount, entries.ToArray()),
        };
        try { Validate(); }
        catch
        {
            Content = previous;
            throw;
        }
        return added;
    }

    public void AddMobyPVar(ProjectMobyPVar pvar)
    {
        ArgumentNullException.ThrowIfNull(pvar);
        var previous = Content;
        var state = Content.MobyPVars
            ?? new ProjectMobyPVarState(ProjectMobyPVarSchema.CurrentVersion, 0, []);
        if (state.Entries.Any(value => value.EntityId == pvar.EntityId))
            throw new InvalidOperationException($"Entity {pvar.EntityId} already has a project PVar.");
        Content = Content with { MobyPVars = state with { Entries = state.Entries.Append(pvar).ToArray() } };
        try { Validate(); }
        catch
        {
            Content = previous;
            throw;
        }
    }

    private static int FindMobyPVarIndex(ProjectMobyPVarState state, EntityId entityId)
    {
        for (var index = 0; index < state.Entries.Count; index++)
            if (state.Entries[index].EntityId == entityId) return index;
        throw new KeyNotFoundException($"Entity {entityId} has no project PVar.");
    }
}
