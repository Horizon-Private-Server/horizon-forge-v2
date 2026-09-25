namespace Forge.Host.Domain;

public sealed class EditorRuntime : IAsyncDisposable
{
    private const int MaxEvents = 1_024;
    private const int MaxDiagnostics = 100;
    private static readonly string[] RuntimeCapabilities =
    [
        "editor.query", "editor.selection", "editor.project.rename", "editor.transform.update",
        "editor.entity.rename", "editor.entity.layer", "editor.entity.state", "editor.entity.delete",
        "editor.entity.duplicate", "editor.level-settings.update", "editor.clipboard", "editor.history",
        "editor.save", "editor.recovery",
    ];
    private static readonly EditorTool[] RuntimeTools =
    [
        new("select", "Select", "editor.selection"),
        new("translate", "Move", "editor.transform.update"),
        new("rotate", "Rotate", "editor.transform.update"),
        new("scale", "Scale", "editor.transform.update"),
    ];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<EditorEvent> _events = [];
    private readonly List<EditorDiagnostic> _diagnostics = [];
    private readonly EditorHistory _history = new();
    private ForgeProjectWorkspace? _workspace;
    private HashSet<AssetId> _missingAssets = [];
    private Dictionary<EntityId, ProjectEntity> _savedEntities = [];
    private ProjectEntity[] _clipboard = [];
    private EntityId[] _selection = [];
    private TimeSpan _autosaveDelay;
    private CancellationTokenSource? _autosaveCancellation;
    private long _eventSequence;
    private bool _disposed;

    public bool HasCapability(string capability) => RuntimeCapabilities.Contains(capability, StringComparer.Ordinal);

    public async Task<EditorSnapshot> OpenAsync(
        string projectPath,
        TimeSpan autosaveDelay,
        CancellationToken cancellationToken = default) =>
        await OpenAsync(projectPath, null, autosaveDelay, cancellationToken);

    public async Task<EditorSnapshot> OpenAsync(
        string projectPath,
        string? catalogRootPath,
        TimeSpan autosaveDelay,
        CancellationToken cancellationToken = default)
    {
        if (autosaveDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(autosaveDelay));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            var workspace = await ForgeProjectWorkspace.OpenAsync(projectPath, cancellationToken);
            var missingAssets = catalogRootPath is null
                ? []
                : FindMissingAssets(workspace, await AssetCatalogStore.OpenAsync(catalogRootPath, cancellationToken));
            await CloseCoreAsync(cancellationToken);
            _workspace = workspace;
            _savedEntities = workspace.Content.Entities.ToDictionary(entity => entity.EntityId);
            _missingAssets = missingAssets;
            _autosaveDelay = autosaveDelay;
            _selection = [];
            _clipboard = [];
            _history.Clear();
            _diagnostics.Clear();
            AddEvent(EditorEventKind.ProjectOpened, null, [], _workspace.RootPath);
            return Snapshot();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            await CloseCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<EditorSnapshot> ExecuteAsync(
        EditorCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            var workspace = RequireWorkspace();
            ValidateCommand(command, workspace);
            var stateBefore = workspace.CaptureState();
            var selectionBefore = _selection;
            var fingerprintBefore = workspace.CurrentFingerprint;
            IReadOnlyList<EntityId> historyEntityIds = command.EntityIds;
            switch (command.Kind)
            {
                case EditorCommandKind.SetSelection:
                    _selection = command.EntityIds.ToArray();
                    AddEvent(EditorEventKind.SelectionChanged, command.Id, _selection, null);
                    break;
                case EditorCommandKind.RenameProject:
                    workspace.Rename(command.Text!);
                    AddEvent(EditorEventKind.ProjectChanged, command.Id, [], "Project renamed");
                    ScheduleAutosave();
                    break;
                case EditorCommandKind.UpdateTransform:
                    workspace.UpdateTransform(command.EntityIds[0], command.Transform!);
                    AddEvent(EditorEventKind.ProjectChanged, command.Id, command.EntityIds, "Transform updated");
                    ScheduleAutosave();
                    break;
                case EditorCommandKind.UpdateTransforms:
                    workspace.UpdateTransforms(command.Transforms!);
                    AddEvent(EditorEventKind.ProjectChanged, command.Id, command.EntityIds, "Transforms updated");
                    ScheduleAutosave();
                    break;
                case EditorCommandKind.RenameEntity:
                    workspace.RenameEntity(command.EntityIds[0], command.Text!);
                    AddEvent(EditorEventKind.ProjectChanged, command.Id, command.EntityIds, "Entity renamed");
                    ScheduleAutosave();
                    break;
                case EditorCommandKind.SetEntityLayer:
                    workspace.SetEntityLayer(command.EntityIds, command.Text!);
                    AddEvent(EditorEventKind.ProjectChanged, command.Id, command.EntityIds, "Entity layer updated");
                    ScheduleAutosave();
                    break;
                case EditorCommandKind.SetEntityState:
                    workspace.SetEntityState(command.EntityIds, command.State!.Hidden, command.State.Disabled, command.State.Locked);
                    AddEvent(EditorEventKind.ProjectChanged, command.Id, command.EntityIds, "Entity state updated");
                    ScheduleAutosave();
                    break;
                case EditorCommandKind.Undo:
                    ApplyHistory(workspace, command.Id, undo: true);
                    break;
                case EditorCommandKind.Redo:
                    ApplyHistory(workspace, command.Id, undo: false);
                    break;
                case EditorCommandKind.DeleteEntities:
                    _selection = workspace.RemoveEntities(command.EntityIds);
                    AddEvent(EditorEventKind.ProjectChanged, command.Id, command.EntityIds, "Entities deleted");
                    ScheduleAutosave();
                    break;
                case EditorCommandKind.DuplicateEntities:
                    _selection = workspace.AddCopies(workspace.GetEntities(command.EntityIds))
                        .Select(entity => entity.EntityId).ToArray();
                    historyEntityIds = _selection;
                    AddEvent(EditorEventKind.ProjectChanged, command.Id, _selection, "Entities duplicated");
                    ScheduleAutosave();
                    break;
                case EditorCommandKind.CopyEntities:
                    _clipboard = workspace.GetEntities(command.EntityIds);
                    break;
                case EditorCommandKind.PasteEntities:
                    if (_clipboard.Length == 0) break;
                    _selection = workspace.AddCopies(_clipboard).Select(entity => entity.EntityId).ToArray();
                    historyEntityIds = _selection;
                    AddEvent(EditorEventKind.ProjectChanged, command.Id, _selection, "Entities pasted");
                    ScheduleAutosave();
                    break;
                case EditorCommandKind.UpdateLevelSettings:
                    workspace.UpdateLevelSettings(command.LevelSettings!);
                    AddEvent(EditorEventKind.ProjectChanged, command.Id, [], "Level settings updated");
                    ScheduleAutosave();
                    break;
                case EditorCommandKind.UpdateSplinePoints:
                    workspace.UpdateSplinePoints(command.EntityIds[0], command.Points!);
                    AddEvent(EditorEventKind.ProjectChanged, command.Id, command.EntityIds, "Spline points updated");
                    ScheduleAutosave();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(command), command.Kind, "Unknown editor command");
            }
            if (command.Kind is not (EditorCommandKind.SetSelection or EditorCommandKind.CopyEntities
                    or EditorCommandKind.Undo or EditorCommandKind.Redo)
                && workspace.CurrentFingerprint != fingerprintBefore)
                _history.Push(stateBefore, workspace.CaptureState(), selectionBefore, _selection,
                    historyEntityIds, EstimateHistoryBytes(command, stateBefore));
            return Snapshot();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<EditorSnapshot> SaveAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            CancelAutosave();
            var workspace = RequireWorkspace();
            await workspace.SaveAsync(cancellationToken);
            _savedEntities = workspace.Content.Entities.ToDictionary(entity => entity.EntityId);
            AddEvent(EditorEventKind.ProjectSaved, null, [], null);
            return Snapshot();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<EditorSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            return Snapshot();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<EditorEvent>> ReadEventsAsync(
        long afterSequence,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (afterSequence < 0) throw new ArgumentOutOfRangeException(nameof(afterSequence));
        if (limit is < 1 or > MaxEvents) throw new ArgumentOutOfRangeException(nameof(limit));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            return _events.Where(value => value.Sequence > afterSequence).Take(limit).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            await CloseCoreAsync(CancellationToken.None);
            _disposed = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task CloseCoreAsync(CancellationToken cancellationToken)
    {
        CancelAutosave();
        if (_workspace is null)
        {
            _history.Clear();
            return;
        }
        var path = _workspace.RootPath;
        await WriteRecoveryCoreAsync(cancellationToken);
        AddEvent(EditorEventKind.ProjectClosed, null, [], path);
        _history.Clear();
        _workspace = null;
        _missingAssets = [];
        _savedEntities = [];
        _selection = [];
        _clipboard = [];
    }

    private async Task WriteRecoveryCoreAsync(CancellationToken cancellationToken)
    {
        if (_workspace is null || !_workspace.IsDirty) return;
        var recovery = await _workspace.WriteRecoveryAsync(cancellationToken);
        if (recovery is not null)
            AddEvent(EditorEventKind.RecoveryWritten, null, [], recovery.Id);
    }

    private void ScheduleAutosave()
    {
        CancelAutosave();
        if (_autosaveDelay == TimeSpan.Zero) return;
        var cancellation = new CancellationTokenSource();
        _autosaveCancellation = cancellation;
        _ = RunAutosaveAsync(cancellation);
    }

    private async Task RunAutosaveAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(_autosaveDelay, cancellation.Token);
            await _gate.WaitAsync(cancellation.Token);
            try
            {
                if (cancellation != _autosaveCancellation) return;
                await WriteRecoveryCoreAsync(cancellation.Token);
                _autosaveCancellation = null;
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await _gate.WaitAsync();
            try
            {
                if (cancellation == _autosaveCancellation) _autosaveCancellation = null;
                AddDiagnostic("autosave.failed", EditorDiagnosticSeverity.Error, exception.Message);
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private void CancelAutosave()
    {
        var cancellation = _autosaveCancellation;
        _autosaveCancellation = null;
        cancellation?.Cancel();
    }

    private EditorSnapshot Snapshot()
    {
        var workspace = RequireWorkspace();
        return new(
            workspace.RootPath,
            workspace.Manifest.ProjectId,
            workspace.Manifest.Name,
            workspace.Manifest.Target,
            workspace.Manifest.BaseLevel,
            workspace.Content.LevelSettings,
            workspace.Content.Entities.Select(entity =>
            {
                var state = entity.State ?? new();
                return new EditorEntitySnapshot(
                    entity.EntityId,
                    entity.Name,
                    entity.Layer,
                    entity.Transform,
                    entity.Asset,
                    entity.Provenance,
                    entity.Source?.ClassId,
                    Visualization(entity),
                    new(!_savedEntities.TryGetValue(entity.EntityId, out var saved) || saved != entity,
                        state.Hidden, state.Disabled, state.Locked,
                        IsReadOnlySource(entity),
                        HasInvalidGeometryLinks(entity),
                        entity.Asset is not null && _missingAssets.Contains(entity.Asset.Id)));
            }).ToArray(),
            _selection.ToArray(),
            workspace.IsDirty,
            workspace.MigrationPending,
            _history.CanUndo,
            _history.CanRedo,
            _clipboard.Length > 0,
            _eventSequence,
            RuntimeCapabilities.ToArray(),
            RuntimeTools.ToArray(),
            _diagnostics.ToArray());
    }

    private static EditorEntityGeometry? Visualization(ProjectEntity entity)
    {
        var geometry = entity.Geometry;
        if (geometry?.Cuboid is not null) return new(EditorGeometryKind.Cuboid, []);
        if (geometry?.Spline is not null) return new(EditorGeometryKind.Spline, geometry.Spline.Points);
        if (geometry?.Area is not null) return new(EditorGeometryKind.Area, []);
        if (geometry?.Sphere is not null) return new(EditorGeometryKind.Sphere, []);
        if (geometry?.Cylinder is not null) return new(EditorGeometryKind.Cylinder, []);
        if (geometry?.Pill is not null) return new(EditorGeometryKind.Pill, []);
        if (geometry?.GrindPath is not null) return new(EditorGeometryKind.GrindPath, geometry.GrindPath.Points);
        var lighting = entity.Lighting;
        if (lighting?.DirectionalLight is { } directional)
            return new(EditorGeometryKind.DirectionalLight,
            [
                new(0, 0, 0, 1),
                new(directional.TopDirection.X * 12, directional.TopDirection.Y * 12,
                    directional.TopDirection.Z * 12, directional.TopDirection.W),
            ]);
        if (lighting?.PointLight is not null) return new(EditorGeometryKind.PointLight, []);
        if (lighting?.EnvironmentSamplePoint is not null) return new(EditorGeometryKind.EnvironmentSample, []);
        if (lighting?.EnvironmentTransition is not null) return new(EditorGeometryKind.EnvironmentTransition, []);
        if (entity.Camera is not null) return new(EditorGeometryKind.Camera, []);
        if (entity.AmbientSound is not null) return new(EditorGeometryKind.AmbientSound, []);
        return null;
    }

    private static bool IsReadOnlySource(ProjectEntity entity) => IsDecodedSource(entity) && !HasTransformWriter(entity);

    private static bool IsDecodedSource(ProjectEntity entity) => entity.Geometry is not null
        || entity.Lighting is not null || entity.Camera is not null || entity.AmbientSound is not null;

    private static bool HasTransformWriter(ProjectEntity entity)
    {
        if (entity.Provenance is not { Game: "UYA" }) return false;
        if (entity.Camera is not null || entity.AmbientSound is not null) return true;
        if (entity.Geometry?.Spline is not null) return true;
        var shape = entity.Geometry is { } geometry
            ? geometry.Cuboid ?? geometry.Sphere ?? geometry.Cylinder ?? geometry.Pill
            : null;
        return shape is not null && shape.CameraCollision is null;
    }

    private static bool HasInvalidGeometryLinks(ProjectEntity entity)
    {
        var area = entity.Geometry?.Area;
        return area is not null && area.Splines.Concat(area.Cuboids).Concat(area.Spheres)
            .Concat(area.Cylinders).Concat(area.NegativeCuboids).Any(link => link.EntityId is null);
    }

    private static void ValidateCommand(EditorCommand command, ForgeProjectWorkspace workspace)
    {
        if (!Guid.TryParseExact(command.Id, "D", out var commandId) || commandId == Guid.Empty
            || commandId.ToString("D") != command.Id)
            throw new ArgumentException("Command ID must be a lowercase canonical UUID.", nameof(command));
        if (!Enum.IsDefined(command.Kind)) throw new ArgumentOutOfRangeException(nameof(command), "Unknown editor command kind.");
        ArgumentNullException.ThrowIfNull(command.EntityIds);
        if (command.EntityIds.Count != command.EntityIds.Distinct().Count())
            throw new ArgumentException("Command Entity IDs must be unique.", nameof(command));
        var known = workspace.Content.Entities.Select(entity => entity.EntityId).ToHashSet();
        if (command.EntityIds.Any(id => !known.Contains(id)))
            throw new ArgumentException("Command references an entity that is not present in the active project.", nameof(command));
        if (command.Kind != EditorCommandKind.UpdateLevelSettings && command.LevelSettings is not null)
            throw new ArgumentException("Only level setting commands can contain level settings.", nameof(command));
        if (command.Kind != EditorCommandKind.UpdateSplinePoints && command.Points is not null)
            throw new ArgumentException("Only spline point commands can contain points.", nameof(command));
        var locked = workspace.Content.Entities
            .Where(entity => entity.State?.Locked == true)
            .Select(entity => entity.EntityId)
            .ToHashSet();
        var readOnly = workspace.Content.Entities
            .Where(IsReadOnlySource)
            .Select(entity => entity.EntityId)
            .ToHashSet();
        var transformOnly = workspace.Content.Entities
            .Where(HasTransformWriter)
            .Select(entity => entity.EntityId)
            .ToHashSet();
        var readOnlyStateChange = command.Kind == EditorCommandKind.SetEntityState
            && command.State is { Locked: null };
        if (command.EntityIds.Any(readOnly.Contains)
            && command.Kind != EditorCommandKind.SetSelection
            && !readOnlyStateChange)
            throw new ArgumentException("Decoded source data is read-only until its native writer is available.", nameof(command));
        if (command.EntityIds.Any(transformOnly.Contains)
            && (command.Kind is EditorCommandKind.DeleteEntities or EditorCommandKind.DuplicateEntities
                or EditorCommandKind.CopyEntities
                || command.Kind == EditorCommandKind.SetEntityState && command.State?.Disabled is not null))
            throw new ArgumentException("This decoded source type supports transform edits but not structural changes.", nameof(command));
        if (command.EntityIds.Any(locked.Contains)
            && command.Kind is EditorCommandKind.UpdateTransform or EditorCommandKind.UpdateTransforms
                or EditorCommandKind.RenameEntity
                or EditorCommandKind.SetEntityLayer
                or EditorCommandKind.DeleteEntities
                or EditorCommandKind.DuplicateEntities
                or EditorCommandKind.UpdateSplinePoints)
            throw new ArgumentException("Locked entities cannot be modified.", nameof(command));
        if (command.EntityIds.Any(locked.Contains)
            && command.Kind == EditorCommandKind.SetEntityState
            && command.State is not { Hidden: null, Disabled: null, Locked: false })
            throw new ArgumentException("Unlock an entity before changing its state.", nameof(command));
        switch (command.Kind)
        {
            case EditorCommandKind.SetSelection when command.Transform is not null || command.Text is not null || command.State is not null:
                throw new ArgumentException("Selection commands cannot contain mutation data.", nameof(command));
            case EditorCommandKind.RenameProject when command.EntityIds.Count != 0 || command.Transform is not null
                || string.IsNullOrWhiteSpace(command.Text) || command.State is not null:
                throw new ArgumentException("Rename commands require only a project name.", nameof(command));
            case EditorCommandKind.UpdateTransform when command.EntityIds.Count != 1 || command.Transform is null
                || command.Text is not null || command.State is not null || command.Transforms?.Count > 0:
                throw new ArgumentException("Transform commands require one entity and a transform.", nameof(command));
            case EditorCommandKind.UpdateTransforms when command.EntityIds.Count == 0 || command.Transform is not null
                || command.Text is not null || command.State is not null || command.Transforms is null
                || command.Transforms.Count != command.EntityIds.Count
                || !command.Transforms.Select(update => update.EntityId).SequenceEqual(command.EntityIds):
                throw new ArgumentException("Batch transform commands require one transform per entity in command order.", nameof(command));
            case EditorCommandKind.RenameEntity when command.EntityIds.Count != 1 || command.Transform is not null
                || string.IsNullOrWhiteSpace(command.Text) || command.State is not null:
                throw new ArgumentException("Entity rename commands require one entity and a name.", nameof(command));
            case EditorCommandKind.SetEntityLayer when command.EntityIds.Count == 0 || command.Transform is not null
                || string.IsNullOrWhiteSpace(command.Text) || command.State is not null:
                throw new ArgumentException("Layer commands require entities and a layer.", nameof(command));
            case EditorCommandKind.SetEntityState when command.EntityIds.Count == 0 || command.Transform is not null
                || command.Text is not null || command.State is null
                || (command.State.Hidden is null && command.State.Disabled is null && command.State.Locked is null):
                throw new ArgumentException("State commands require entities and at least one state change.", nameof(command));
            case EditorCommandKind.Undo or EditorCommandKind.Redo when command.EntityIds.Count != 0
                || command.Transform is not null || command.Text is not null || command.State is not null
                || command.Transforms?.Count > 0:
                throw new ArgumentException("History commands cannot contain mutation data.", nameof(command));
            case EditorCommandKind.DeleteEntities or EditorCommandKind.DuplicateEntities or EditorCommandKind.CopyEntities
                when command.EntityIds.Count == 0 || command.Transform is not null || command.Text is not null
                || command.State is not null || command.Transforms?.Count > 0:
                throw new ArgumentException("Entity edit commands require only one or more entities.", nameof(command));
            case EditorCommandKind.PasteEntities when command.EntityIds.Count != 0 || command.Transform is not null
                || command.Text is not null || command.State is not null || command.Transforms?.Count > 0:
                throw new ArgumentException("Paste commands cannot contain mutation data.", nameof(command));
            case EditorCommandKind.UpdateLevelSettings when command.EntityIds.Count != 0 || command.Transform is not null
                || command.Text is not null || command.State is not null || command.Transforms?.Count > 0
                || command.LevelSettings is null:
                throw new ArgumentException("Level setting commands require only level settings.", nameof(command));
            case EditorCommandKind.UpdateSplinePoints when command.EntityIds.Count != 1 || command.Transform is not null
                || command.Text is not null || command.State is not null || command.Transforms?.Count > 0
                || command.LevelSettings is not null || command.Points is null
                || workspace.Content.Entities.Single(entity => entity.EntityId == command.EntityIds[0]).Geometry?.Spline is null:
                throw new ArgumentException("Spline point commands require one spline and its points.", nameof(command));
        }
    }

    private void ApplyHistory(ForgeProjectWorkspace workspace, string commandId, bool undo)
    {
        ForgeProjectState state;
        EntityId[] selection;
        EntityId[] entityIds;
        var changed = undo
            ? _history.TryUndo(out state, out selection, out entityIds)
            : _history.TryRedo(out state, out selection, out entityIds);
        if (!changed) return;
        workspace.RestoreState(state);
        _selection = selection;
        AddEvent(EditorEventKind.ProjectChanged, commandId, entityIds, undo ? "Undo" : "Redo");
        if (workspace.IsDirty) ScheduleAutosave();
        else CancelAutosave();
    }

    private static long EstimateHistoryBytes(EditorCommand command, ForgeProjectState before) =>
        256L + (long)before.Content.Entities.Count * IntPtr.Size
        + (long)command.EntityIds.Count * 16
        + (long)(command.Transforms?.Count ?? 0) * 64
        + (long)(command.Points?.Count ?? 0) * 16
        + (command.Text?.Length ?? 0) * sizeof(char);

    private static HashSet<AssetId> FindMissingAssets(ForgeProjectWorkspace workspace, AssetCatalogStore catalog) =>
        workspace.Content.Entities
            .Where(entity => entity.Asset is not null)
            .GroupBy(entity => entity.Asset!.Id)
            .Where(group => workspace.ResolveAssetPath(group.Key, catalog) is null)
            .Select(group => group.Key)
            .ToHashSet();

    private void AddEvent(
        EditorEventKind kind,
        string? commandId,
        IReadOnlyList<EntityId> entityIds,
        string? message)
    {
        _events.Add(new(++_eventSequence, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), kind, commandId,
            entityIds.ToArray(), message));
        if (_events.Count > MaxEvents) _events.RemoveRange(0, _events.Count - MaxEvents);
    }

    private void AddDiagnostic(string code, EditorDiagnosticSeverity severity, string message)
    {
        _diagnostics.Add(new(code, severity, message));
        if (_diagnostics.Count > MaxDiagnostics) _diagnostics.RemoveRange(0, _diagnostics.Count - MaxDiagnostics);
        AddEvent(EditorEventKind.DiagnosticRaised, null, [], code);
    }

    private ForgeProjectWorkspace RequireWorkspace() =>
        _workspace ?? throw new InvalidOperationException("No editor project is open.");

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
