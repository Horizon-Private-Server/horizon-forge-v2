namespace Forge.Host.Domain;

public sealed partial class EditorRuntime
{
    internal async Task<EditorSnapshot> CommitContentImportRemovalAsync(
        ContentImportRemovalCommit command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            var workspace = RequireWorkspace();
            var before = workspace.CaptureState();
            var selectionBefore = _selection;
            workspace.CommitContentImportRemoval(command);
            var changedEntities = ChangedEntityIds(before.Content.Entities, workspace.Content.Entities);
            var remaining = workspace.Content.Entities.Select(value => value.EntityId).ToHashSet();
            _selection = _selection.Where(remaining.Contains).ToArray();
            _history.Push(
                before,
                workspace.CaptureState(),
                selectionBefore,
                _selection,
                changedEntities,
                (long)changedEntities.Length * 512);
            _projectVersion++;
            _groupTemplatePreviews = [];
            AddEvent(EditorEventKind.ProjectChanged, command.BatchId, changedEntities, "Import content removed");
            ScheduleAutosave();
            return Snapshot();
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<EditorSnapshot> CommitContentImportAsync(
        ContentImportBatchCommit command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            var workspace = RequireWorkspace();
            var before = workspace.CaptureState();
            var selectionBefore = _selection;
            await workspace.CommitContentImportAsync(command, cancellationToken);
            var changedEntities = ChangedEntityIds(before.Content.Entities, workspace.Content.Entities);
            var remaining = workspace.Content.Entities.Select(value => value.EntityId).ToHashSet();
            _selection = _selection.Where(remaining.Contains).ToArray();
            _history.Push(
                before,
                workspace.CaptureState(),
                selectionBefore,
                _selection,
                changedEntities,
                command.Attachments.Sum(value => (long)value.CanonicalBytes.Length)
                    + (long)changedEntities.Length * 512);
            _projectVersion++;
            _groupTemplatePreviews = [];
            AddEvent(EditorEventKind.ProjectChanged, command.Batch.BatchId, changedEntities, "Content import committed");
            ScheduleAutosave();
            return Snapshot();
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static EntityId[] ChangedEntityIds(
        IReadOnlyList<ProjectEntity> before,
        IReadOnlyList<ProjectEntity> after)
    {
        var previous = before.ToDictionary(value => value.EntityId);
        var current = after.ToDictionary(value => value.EntityId);
        return before.Select(value => value.EntityId)
            .Union(after.Select(value => value.EntityId))
            .Where(id => !previous.TryGetValue(id, out var left)
                || !current.TryGetValue(id, out var right)
                || left != right)
            .ToArray();
    }
}
