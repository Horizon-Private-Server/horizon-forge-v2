namespace Forge.Host.Domain;

public sealed partial class ForgeProjectWorkspace
{
    public ProjectGroup CreateGroup(string name)
    {
        var group = new ProjectGroup(GroupId.New(), name, []);
        ReplaceGroups([.. Content.Groups, group]);
        return group;
    }

    public void RenameGroup(GroupId groupId, string name)
    {
        var index = FindGroupIndex(groupId);
        var groups = Content.Groups.ToArray();
        groups[index] = groups[index] with { Name = name };
        ReplaceGroups(groups);
    }

    public void DeleteGroup(GroupId groupId)
    {
        var index = FindGroupIndex(groupId);
        var groups = Content.Groups.ToList();
        groups.RemoveAt(index);
        ReplaceGroups(groups);
    }

    public void ReorderGroup(GroupId groupId, int destinationOrder)
    {
        var sourceOrder = FindGroupIndex(groupId);
        if (destinationOrder < 0 || destinationOrder >= Content.Groups.Count)
            throw new ArgumentOutOfRangeException(nameof(destinationOrder));
        if (sourceOrder == destinationOrder) return;
        var groups = Content.Groups.ToList();
        var group = groups[sourceOrder];
        groups.RemoveAt(sourceOrder);
        groups.Insert(destinationOrder, group);
        ReplaceGroups(groups);
    }

    public void AddGroupMembers(GroupId groupId, IReadOnlyList<EntityId> entityIds)
    {
        ArgumentNullException.ThrowIfNull(entityIds);
        if (entityIds.Count == 0 || entityIds.Distinct().Count() != entityIds.Count)
            throw new ArgumentException("Group member additions must contain unique entities.", nameof(entityIds));
        _ = EntityIndexes(entityIds);
        var index = FindGroupIndex(groupId);
        var group = Content.Groups[index];
        if (entityIds.Any(group.Members.Contains))
            throw new InvalidOperationException("An entity is already a member of this group.");
        var groups = Content.Groups.ToArray();
        groups[index] = group with { Members = [.. group.Members, .. entityIds] };
        ReplaceGroups(groups);
    }

    public void RemoveGroupMembers(GroupId groupId, IReadOnlyList<EntityId> entityIds)
    {
        ArgumentNullException.ThrowIfNull(entityIds);
        if (entityIds.Count == 0 || entityIds.Distinct().Count() != entityIds.Count)
            throw new ArgumentException("Group member removals must contain unique entities.", nameof(entityIds));
        var index = FindGroupIndex(groupId);
        var group = Content.Groups[index];
        if (entityIds.Any(entityId => !group.Members.Contains(entityId)))
            throw new InvalidOperationException("An entity is not a member of this group.");
        var removed = entityIds.ToHashSet();
        var groups = Content.Groups.ToArray();
        groups[index] = group with
        {
            Members = group.Members.Where(entityId => !removed.Contains(entityId)).ToArray(),
        };
        ReplaceGroups(groups);
    }

    private int FindGroupIndex(GroupId groupId)
    {
        for (var index = 0; index < Content.Groups.Count; index++)
            if (Content.Groups[index].GroupId == groupId) return index;
        throw new KeyNotFoundException($"Project group {groupId} does not exist.");
    }

    private void ReplaceGroups(IReadOnlyList<ProjectGroup> groups)
    {
        var replacement = Content with { Groups = groups.ToArray() };
        ForgeProjectValidation.Validate(RootPath, Manifest, replacement);
        Content = replacement;
    }
}
