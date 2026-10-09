namespace Forge.Host.Domain;

public sealed partial class EditorRuntime : IAsyncDisposable
{
    private const int MaxEvents = 1_024;
    private const int MaxDiagnostics = 100;
    private static readonly string[] RuntimeCapabilities =
    [
        "editor.query", "editor.selection", "editor.project.rename", "editor.transform.update",
        "editor.entity.rename", "editor.entity.layer", "editor.entity.state", "editor.entity.delete",
        "editor.entity.duplicate", "editor.level-settings.update", "editor.clipboard", "editor.history",
        "editor.save", "editor.recovery",
        "editor.asset.create", "editor.sky-shell.add", "editor.sky-shell.update", "editor.sky-shell.reorder",
        "editor.palette-optimization.update",
        "editor.hud.replace", "editor.hud.append", "editor.hud.remove",
        "editor.reference.update",
        "editor.instanced-collision.inspect", "editor.instanced-collision.preview", "editor.instanced-collision.apply", "editor.instanced-collision.remove",
        "editor.instanced-collision.toggle", "editor.instanced-collision.raw-type", "editor.instanced-collision.face-types",
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
    private readonly EditorAssetPlacementResolver? _placementResolver;
    private readonly EditorTransformCapabilityResolver? _transformCapabilityResolver;
    private readonly EditorSkyShellCommandExecutor? _skyShellCommandExecutor;
    private readonly EditorHudCommandExecutor? _hudCommandExecutor;
    private readonly EditorFxCommandExecutor? _fxCommandExecutor;
    private readonly ushort _hudMinimumAppendSpriteId;
    private readonly ushort _hudMaximumAppendSpriteId;
    private ForgeProjectWorkspace? _workspace;
    private HashSet<AssetId> _missingAssets = [];
    private Dictionary<EntityId, ProjectEntity> _savedEntities = [];
    private ProjectEntity[] _clipboard = [];
    private EntityId[] _selection = [];
    private TimeSpan _autosaveDelay;
    private CancellationTokenSource? _autosaveCancellation;
    private long _eventSequence;
    private long _projectVersion;
    private bool _disposed;
    private string? _catalogRootPath;

    public EditorRuntime(
        EditorAssetPlacementResolver? placementResolver = null,
        EditorTransformCapabilityResolver? transformCapabilityResolver = null,
        EditorSkyShellCommandExecutor? skyShellCommandExecutor = null,
        EditorInstancedCollisionPreviewExecutor? instancedCollisionPreviewExecutor = null,
        EditorInstancedCollisionSourceInspector? instancedCollisionSourceInspector = null,
        EditorInstancedCollisionFaceCountResolver? instancedCollisionFaceCountResolver = null,
        EditorHudCommandExecutor? hudCommandExecutor = null,
        ushort hudMinimumAppendSpriteId = 0,
        ushort hudMaximumAppendSpriteId = 0,
        EditorFxCommandExecutor? fxCommandExecutor = null)
    {
        _placementResolver = placementResolver;
        _transformCapabilityResolver = transformCapabilityResolver;
        _skyShellCommandExecutor = skyShellCommandExecutor;
        _instancedCollisionPreviewExecutor = instancedCollisionPreviewExecutor;
        _instancedCollisionSourceInspector = instancedCollisionSourceInspector;
        _instancedCollisionFaceCountResolver = instancedCollisionFaceCountResolver;
        _hudCommandExecutor = hudCommandExecutor;
        _hudMinimumAppendSpriteId = hudMinimumAppendSpriteId;
        _hudMaximumAppendSpriteId = hudMaximumAppendSpriteId;
        _fxCommandExecutor = fxCommandExecutor;
    }

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
            var catalog = catalogRootPath is null
                ? null
                : await AssetCatalogStore.OpenAsync(catalogRootPath, cancellationToken);
            var missingAssets = catalog is null ? [] : FindMissingAssets(workspace, catalog);
            var instancedCollisionDiagnostics = await InspectInstancedCollisionProxiesAsync(
                workspace, catalog, cancellationToken);
            await CloseCoreAsync(cancellationToken);
            _workspace = workspace;
            _catalogRootPath = catalogRootPath;
            _savedEntities = workspace.Content.Entities.ToDictionary(entity => entity.EntityId);
            _savedInstancedCollisionBindings = workspace.Content.InstancedCollisionBindings
                .ToDictionary(binding => (binding.SourceAssetId, binding.InstanceEntityId));
            _missingAssets = missingAssets;
            _autosaveDelay = autosaveDelay;
            _selection = [];
            _clipboard = [];
            _instancedCollisionCandidates = [];
            _projectVersion = 0;
            _history.Clear();
            _diagnostics.Clear();
            _diagnostics.AddRange(instancedCollisionDiagnostics);
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
                    workspace.UpdateTransform(command.EntityIds[0], EffectiveTransform(
                        workspace.GetEntities(command.EntityIds)[0], command.Transform!));
                    AddEvent(EditorEventKind.ProjectChanged, command.Id, command.EntityIds, "Transform updated");
                    ScheduleAutosave();
                    break;
                case EditorCommandKind.UpdateTransforms:
                    var byId = workspace.GetEntities(command.EntityIds).ToDictionary(entity => entity.EntityId);
                    workspace.UpdateTransforms(command.Transforms!.Select(update => new EditorTransformUpdate(
                        update.EntityId, EffectiveTransform(byId[update.EntityId], update.Transform))).ToArray());
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
                    var changesSkyComposition = command.State!.Disabled is not null
                        && workspace.GetEntities(command.EntityIds).Any(entity => entity.SkyShell is not null);
                    workspace.SetEntityState(command.EntityIds, command.State!.Hidden, command.State.Disabled, command.State.Locked);
                    if (changesSkyComposition)
                        await ValidateSkyMutationAsync(
                            workspace, command, stateBefore, selectionBefore, cancellationToken);
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
                    var deletesSkyShell = workspace.GetEntities(command.EntityIds).Any(entity => entity.SkyShell is not null);
                    _selection = workspace.RemoveEntities(command.EntityIds);
                    if (deletesSkyShell)
                        await ValidateSkyMutationAsync(
                            workspace, command, stateBefore, selectionBefore, cancellationToken);
                    AddEvent(EditorEventKind.ProjectChanged, command.Id, command.EntityIds, "Entities deleted");
                    ScheduleAutosave();
                    break;
                case EditorCommandKind.DuplicateEntities:
                    var duplicatesSkyShell = workspace.GetEntities(command.EntityIds).Any(entity => entity.SkyShell is not null);
                    _selection = workspace.AddCopies(workspace.GetEntities(command.EntityIds))
                        .Select(entity => entity.EntityId).ToArray();
                    if (duplicatesSkyShell)
                        await ValidateSkyMutationAsync(
                            workspace, command, stateBefore, selectionBefore, cancellationToken);
                    historyEntityIds = _selection;
                    AddEvent(EditorEventKind.ProjectChanged, command.Id, _selection, "Entities duplicated");
                    ScheduleAutosave();
                    break;
                case EditorCommandKind.CopyEntities:
                    _clipboard = workspace.GetEntities(command.EntityIds);
                    break;
                case EditorCommandKind.PasteEntities:
                    if (_clipboard.Length == 0) break;
                    var pastesSkyShell = _clipboard.Any(entity => entity.SkyShell is not null);
                    _selection = workspace.AddCopies(_clipboard).Select(entity => entity.EntityId).ToArray();
                    if (pastesSkyShell)
                        await ValidateSkyMutationAsync(
                            workspace, command, stateBefore, selectionBefore, cancellationToken);
                    historyEntityIds = _selection;
                    AddEvent(EditorEventKind.ProjectChanged, command.Id, _selection, "Entities pasted");
                    ScheduleAutosave();
                    break;
                case EditorCommandKind.UpdateLevelSettings:
                    workspace.UpdateLevelSettings(command.LevelSettings!);
                    AddEvent(EditorEventKind.ProjectChanged, command.Id, [], "Level settings updated");
                    ScheduleAutosave();
                    break;
                case EditorCommandKind.UpdatePaletteOptimization:
                    workspace.UpdatePaletteOptimization(command.PaletteOptimization!);
                    AddEvent(EditorEventKind.ProjectChanged, command.Id, [], "Palette optimization updated");
                    ScheduleAutosave();
                    break;
                case EditorCommandKind.UpdateSplinePoints:
                    workspace.UpdateSplinePoints(command.EntityIds[0], command.Points!);
                    AddEvent(EditorEventKind.ProjectChanged, command.Id, command.EntityIds, "Spline points updated");
                    ScheduleAutosave();
                    break;
                case EditorCommandKind.CreateEntityFromAsset:
                    if (_placementResolver is null || _catalogRootPath is null)
                        throw new InvalidOperationException("Asset placement is unavailable.");
                    var placed = await _placementResolver(
                        workspace, _catalogRootPath, command.Placement!, cancellationToken);
                    workspace.AddEntity(placed);
                    _selection = [placed.EntityId];
                    historyEntityIds = _selection;
                    AddEvent(EditorEventKind.ProjectChanged, command.Id, _selection, "Asset placed");
                    ScheduleAutosave();
                    break;
                case EditorCommandKind.AddSkyShellFromAsset:
                case EditorCommandKind.UpdateSkyShell:
                case EditorCommandKind.ReorderSkyShell:
                    if (_skyShellCommandExecutor is null || _catalogRootPath is null)
                        throw new InvalidOperationException("Sky shell editing is unavailable.");
                    var changedSkyShells = await _skyShellCommandExecutor(
                        workspace, _catalogRootPath, command, cancellationToken);
                    if (command.Kind == EditorCommandKind.AddSkyShellFromAsset)
                        _selection = changedSkyShells.ToArray();
                    historyEntityIds = changedSkyShells;
                    AddEvent(EditorEventKind.ProjectChanged, command.Id, changedSkyShells,
                        command.Kind switch
                        {
                            EditorCommandKind.AddSkyShellFromAsset => "Sky shell added",
                            EditorCommandKind.UpdateSkyShell => "Sky shell updated",
                            _ => "Sky shells reordered",
                        });
                    ScheduleAutosave();
                    break;
                case EditorCommandKind.RemoveInstancedCollisionProxy:
                case EditorCommandKind.SetInstancedCollisionEnabled:
                case EditorCommandKind.SetInstancedCollisionRawType:
                case EditorCommandKind.SetInstancedCollisionFaceTypes:
                    historyEntityIds = await ExecuteInstancedCollisionCommandAsync(
                        workspace, command, cancellationToken);
                    break;
                case EditorCommandKind.SetEntityReference:
                    workspace.UpdateEntityReference(
                        command.EntityIds[0],
                        command.ReferenceUpdate!.FieldKey,
                        command.ReferenceUpdate.SourceValue,
                        command.ReferenceUpdate.TargetEntityId);
                    AddEvent(EditorEventKind.ProjectChanged, command.Id, command.EntityIds, "Reference updated");
                    ScheduleAutosave();
                    break;
                case EditorCommandKind.ReplaceHudTexture:
                case EditorCommandKind.RemoveHudTextureOverride:
                case EditorCommandKind.AddHudIcon:
                case EditorCommandKind.RemoveHudIcon:
                    if (_hudCommandExecutor is null)
                        throw new InvalidOperationException("HUD editing is unavailable.");
                    await _hudCommandExecutor(workspace, command, cancellationToken);
                    AddEvent(EditorEventKind.ProjectChanged, command.Id, [], command.Kind switch
                    {
                        EditorCommandKind.ReplaceHudTexture => "HUD texture replaced",
                        EditorCommandKind.RemoveHudTextureOverride => "HUD texture override removed",
                        EditorCommandKind.AddHudIcon => "HUD icon added",
                        _ => "HUD icon removed",
                    });
                    ScheduleAutosave();
                    break;
                case EditorCommandKind.ReplaceFxTexture:
                case EditorCommandKind.RemoveFxTextureOverride:
                case EditorCommandKind.AddFxTexture:
                case EditorCommandKind.RemoveFxTexture:
                    if (_fxCommandExecutor is null)
                        throw new InvalidOperationException("FX texture editing is unavailable.");
                    await _fxCommandExecutor(workspace, command, cancellationToken);
                    AddEvent(EditorEventKind.ProjectChanged, command.Id, [], command.Kind switch
                    {
                        EditorCommandKind.ReplaceFxTexture => "FX texture replaced",
                        EditorCommandKind.RemoveFxTextureOverride => "FX texture override removed",
                        EditorCommandKind.AddFxTexture => "FX texture added",
                        _ => "FX texture removed",
                    });
                    ScheduleAutosave();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(command), command.Kind, "Unknown editor command");
            }
            var projectChanged = workspace.CurrentFingerprint != fingerprintBefore;
            if (projectChanged)
            {
                _projectVersion++;
                if (command.Kind == EditorCommandKind.SetInstancedCollisionRawType)
                {
                    var sourceAssetId = workspace.GetEntities(command.EntityIds)[0].Asset!.Id;
                    _instancedCollisionCandidates = _instancedCollisionCandidates
                        .Where(value => value.Value.SourceAssetId == sourceAssetId)
                        .ToDictionary(
                            value => value.Key,
                            value => value.Value with
                            {
                                ProjectVersion = _projectVersion,
                                Candidate = value.Value.Candidate with
                                {
                                    Recipe = value.Value.Candidate.Recipe with
                                    {
                                        RawType = command.InstancedCollisionRawType!.Value,
                                    },
                                },
                            },
                            StringComparer.Ordinal);
                }
            }
            if (command.Kind is not (EditorCommandKind.SetSelection or EditorCommandKind.CopyEntities
                    or EditorCommandKind.Undo or EditorCommandKind.Redo) && projectChanged)
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
            _savedInstancedCollisionBindings = workspace.Content.InstancedCollisionBindings
                .ToDictionary(binding => (binding.SourceAssetId, binding.InstanceEntityId));
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
        _savedInstancedCollisionBindings = [];
        _selection = [];
        _clipboard = [];
        _catalogRootPath = null;
        _instancedCollisionCandidates = [];
        _projectVersion = 0;
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
        var references = ReferenceSnapshots(workspace);
        var bindings = workspace.Content.InstancedCollisionBindings.ToDictionary(
            binding => (binding.SourceAssetId, binding.InstanceEntityId));
        return new(
            workspace.RootPath,
            workspace.Manifest.ProjectId,
            workspace.Manifest.Name,
            workspace.Manifest.Target,
            workspace.Manifest.BaseLevel,
            workspace.Content.LevelSettings,
            HudSnapshot(workspace),
            FxSnapshot(workspace),
            workspace.Content.Entities.Select(entity =>
            {
                var state = entity.State ?? new();
                var sharedBindingKey = (entity.Asset?.Id ?? default, (EntityId?)null);
                var individualBindingKey = (entity.Asset?.Id ?? default, (EntityId?)entity.EntityId);
                var bindingDirty = entity.Asset.IsInstancedCollisionSource()
                    && (!Equals(
                            _savedInstancedCollisionBindings.GetValueOrDefault(sharedBindingKey),
                            bindings.GetValueOrDefault(sharedBindingKey))
                        || !Equals(
                            _savedInstancedCollisionBindings.GetValueOrDefault(individualBindingKey),
                            bindings.GetValueOrDefault(individualBindingKey)));
                return new EditorEntitySnapshot(
                    entity.EntityId,
                    entity.Name,
                    entity.Layer,
                    entity.Transform,
                    entity.Asset,
                    entity.Provenance,
                    entity.Source?.ClassId,
                    Visualization(entity),
                    TransformCapabilities(entity),
                    new(!_savedEntities.TryGetValue(entity.EntityId, out var saved) || saved != entity || bindingDirty,
                        state.Hidden, state.Disabled, state.Locked,
                        IsReadOnlySource(entity),
                        HasInvalidGeometryLinks(entity),
                        entity.Asset is not null && _missingAssets.Contains(entity.Asset.Id)),
                    entity.SkyShell,
                    entity.Collision,
                    entity.Asset is { } sharedAsset && sharedAsset.IsInstancedCollisionSource()
                        && bindings.TryGetValue((sharedAsset.Id, null), out var sharedBinding)
                            ? new(sharedBinding.ProxyAssetId, sharedBinding.Recipe, sharedBinding.FaceTypeOverrides)
                            : null,
                    entity.Asset is { } individualAsset && individualAsset.IsInstancedCollisionSource()
                        && bindings.TryGetValue((individualAsset.Id, entity.EntityId), out var individualBinding)
                            ? new(individualBinding.ProxyAssetId, individualBinding.Recipe,
                                individualBinding.FaceTypeOverrides)
                            : null,
                    entity.Asset.IsInstancedCollisionSource()
                        ? entity.InstancedCollisionEnabled
                        : null);
            }).ToArray(),
            references,
            _selection.ToArray(),
            workspace.IsDirty,
            _history.CanUndo,
            _history.CanRedo,
            _clipboard.Length > 0,
            _eventSequence,
            RuntimeCapabilities.ToArray(),
            RuntimeTools.ToArray(),
            _diagnostics.Concat(PlacementDiagnostics(workspace)).TakeLast(MaxDiagnostics).ToArray());
    }

    private static IEnumerable<EditorDiagnostic> PlacementDiagnostics(ForgeProjectWorkspace workspace) =>
        workspace.Content.Entities
            .Where(entity => entity.Asset?.Kind == AssetKind.Moby && entity.Provenance is null
                && entity.Source?.RawRecord.Length >= 0x6c
                && System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(
                    entity.Source.RawRecord.AsSpan(0x68)) == -1)
            .Select(entity => new EditorDiagnostic(
                "moby.pvar-not-generated",
                EditorDiagnosticSeverity.Warning,
                $"{entity.Name}: No generated Pvar data; class-specific behavior may be unavailable."));

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

    private bool IsReadOnlySource(ProjectEntity entity) => entity.SkyShell is null
        && IsDecodedSource(entity) && TransformCapabilities(entity) == EditorTransformCapabilities.None;

    private static bool IsDecodedSource(ProjectEntity entity) => entity.Geometry is not null
        || entity.Lighting is not null || entity.Camera is not null || entity.AmbientSound is not null
        || entity.SkyShell is not null || entity.Collision is not null;

    private EditorTransformCapabilities TransformCapabilities(ProjectEntity entity)
    {
        const EditorTransformCapabilities all = EditorTransformCapabilities.Translate
            | EditorTransformCapabilities.Rotate | EditorTransformCapabilities.Scale;
        if (!IsDecodedSource(entity)) return all;
        return _transformCapabilityResolver?.Invoke(entity) ?? EditorTransformCapabilities.None;
    }

    private static bool HasInvalidGeometryLinks(ProjectEntity entity)
    {
        var area = entity.Geometry?.Area;
        return area is not null && area.Splines.Concat(area.Cuboids).Concat(area.Spheres)
            .Concat(area.Cylinders).Concat(area.NegativeCuboids)
            .Any(link => link.Reference?.EntityId is null);
    }

    private void ValidateCommand(EditorCommand command, ForgeProjectWorkspace workspace)
    {
        ValidateCommandId(command.Id);
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
        if (command.Kind != EditorCommandKind.CreateEntityFromAsset && command.Placement is not null)
            throw new ArgumentException("Only asset placement commands can contain placement data.", nameof(command));
        if (command.Kind != EditorCommandKind.AddSkyShellFromAsset && command.SkyShellSource is not null)
            throw new ArgumentException("Only sky shell add commands can contain a source.", nameof(command));
        if (command.Kind != EditorCommandKind.UpdateSkyShell && command.SkyShellUpdate is not null)
            throw new ArgumentException("Only sky shell update commands can contain rotation values.", nameof(command));
        if (command.Kind != EditorCommandKind.ReorderSkyShell && command.DestinationOrder is not null)
            throw new ArgumentException("Only sky shell reorder commands can contain an order.", nameof(command));
        if (command.Kind != EditorCommandKind.SetInstancedCollisionEnabled && command.InstancedCollisionEnabled is not null)
            throw new ArgumentException("Only instanced collision toggle commands can contain an enabled value.", nameof(command));
        if (command.Kind != EditorCommandKind.SetInstancedCollisionRawType && command.InstancedCollisionRawType is not null)
            throw new ArgumentException("Only instanced collision ID commands can contain a raw type.", nameof(command));
        if (command.Kind != EditorCommandKind.SetInstancedCollisionFaceTypes
            && (command.InstancedCollisionProxyAssetId is not null || command.InstancedCollisionFaceTypes is not null))
            throw new ArgumentException("Only instanced collision face commands can contain face assignments.", nameof(command));
        if (command.Kind != EditorCommandKind.SetEntityReference && command.ReferenceUpdate is not null)
            throw new ArgumentException("Only reference commands can contain a reference update.", nameof(command));
        if (command.Kind != EditorCommandKind.UpdatePaletteOptimization && command.PaletteOptimization is not null)
            throw new ArgumentException(
                "Only palette optimization commands can contain a palette profile.", nameof(command));
        if (command.Kind is not (EditorCommandKind.ReplaceHudTexture
                or EditorCommandKind.RemoveHudTextureOverride
                or EditorCommandKind.AddHudIcon
                or EditorCommandKind.RemoveHudIcon)
            && command.HudEdit is not null)
            throw new ArgumentException("Only HUD commands can contain a HUD edit.", nameof(command));
        if (command.Kind is not (EditorCommandKind.ReplaceFxTexture
                or EditorCommandKind.RemoveFxTextureOverride
                or EditorCommandKind.AddFxTexture
                or EditorCommandKind.RemoveFxTexture)
            && command.FxEdit is not null)
            throw new ArgumentException("Only FX commands can contain an FX edit.", nameof(command));
        var locked = workspace.Content.Entities
            .Where(entity => entity.State?.Locked == true)
            .Select(entity => entity.EntityId)
            .ToHashSet();
        var readOnly = workspace.Content.Entities
            .Where(IsReadOnlySource)
            .Select(entity => entity.EntityId)
            .ToHashSet();
        var transformOnly = workspace.Content.Entities
            .Where(entity => IsDecodedSource(entity)
                && TransformCapabilities(entity) != EditorTransformCapabilities.None)
            .Select(entity => entity.EntityId)
            .ToHashSet();
        var readOnlyStateChange = command.Kind == EditorCommandKind.SetEntityState
            && command.State is { Locked: null };
        if (command.EntityIds.Any(readOnly.Contains)
            && command.Kind != EditorCommandKind.SetSelection
            && !readOnlyStateChange)
            throw new ArgumentException("Decoded source data is read-only until its native writer is available.", nameof(command));
        if (command.EntityIds.Any(transformOnly.Contains)
            && workspace.GetEntities(command.EntityIds).Any(entity => entity.Collision is null)
            && (command.Kind is EditorCommandKind.DeleteEntities or EditorCommandKind.DuplicateEntities
                or EditorCommandKind.CopyEntities
                || command.Kind == EditorCommandKind.SetEntityState && command.State?.Disabled is not null))
            throw new ArgumentException("This decoded source type supports transform edits but not structural changes.", nameof(command));
        if (command.EntityIds.Count > 0
            && workspace.GetEntities(command.EntityIds).Any(entity => entity.Collision is not null)
            && command.Kind is EditorCommandKind.DuplicateEntities or EditorCommandKind.CopyEntities
                or EditorCommandKind.SetEntityLayer)
            throw new ArgumentException("Collision pieces cannot be duplicated, copied, or moved to another layer.", nameof(command));
        var movesLockedCollisionParent = (command.Kind is EditorCommandKind.UpdateTransform
                or EditorCommandKind.UpdateTransforms)
            && workspace.GetEntities(command.EntityIds).Any(entity =>
                entity.Collision?.Attachment is { } attachment && locked.Contains(attachment.ParentEntityId));
        if ((command.EntityIds.Any(locked.Contains) || movesLockedCollisionParent)
            && command.Kind is EditorCommandKind.UpdateTransform or EditorCommandKind.UpdateTransforms
                or EditorCommandKind.RenameEntity
                or EditorCommandKind.SetEntityLayer
                or EditorCommandKind.DeleteEntities
                or EditorCommandKind.DuplicateEntities
                or EditorCommandKind.UpdateSplinePoints
                or EditorCommandKind.UpdateSkyShell
                or EditorCommandKind.ReorderSkyShell
                or EditorCommandKind.RemoveInstancedCollisionProxy
                or EditorCommandKind.SetInstancedCollisionEnabled
                or EditorCommandKind.SetInstancedCollisionRawType
                or EditorCommandKind.SetInstancedCollisionFaceTypes
                or EditorCommandKind.SetEntityReference)
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
            case EditorCommandKind.UpdatePaletteOptimization when command.EntityIds.Count != 0
                || command.Transform is not null || command.Text is not null || command.State is not null
                || command.Transforms?.Count > 0 || command.PaletteOptimization is null:
                throw new ArgumentException(
                    "Palette optimization commands require only a palette profile.", nameof(command));
            case EditorCommandKind.UpdateSplinePoints when command.EntityIds.Count != 1 || command.Transform is not null
                || command.Text is not null || command.State is not null || command.Transforms?.Count > 0
                || command.LevelSettings is not null || command.Points is null
                || !IsEditablePath(workspace.Content.Entities.Single(
                    entity => entity.EntityId == command.EntityIds[0])):
                throw new ArgumentException("Path point commands require one editable path and its points.", nameof(command));
            case EditorCommandKind.CreateEntityFromAsset when command.EntityIds.Count != 0
                || command.Transform is not null || command.Text is not null || command.State is not null
                || command.Transforms?.Count > 0 || command.LevelSettings is not null || command.Points is not null
                || command.Placement is null:
                throw new ArgumentException("Asset placement commands require only placement data.", nameof(command));
            case EditorCommandKind.AddSkyShellFromAsset when command.EntityIds.Count != 0
                || command.Transform is not null || command.Text is not null || command.State is not null
                || command.Transforms?.Count > 0 || command.LevelSettings is not null || command.Points is not null
                || command.Placement is not null || command.SkyShellSource is null
                || command.SkyShellSource.ShellIndex < 0:
                throw new ArgumentException("Sky shell add commands require only a source asset and shell index.", nameof(command));
            case EditorCommandKind.UpdateSkyShell when command.EntityIds.Count != 1
                || command.Transform is not null || command.Text is not null || command.State is not null
                || command.Transforms?.Count > 0 || command.LevelSettings is not null || command.Points is not null
                || command.Placement is not null || command.SkyShellUpdate is null
                || command.SkyShellUpdate.InitialRotationRadians is null
                    && command.SkyShellUpdate.AngularVelocityRadiansPerSecond is null
                || workspace.Content.Entities.Single(entity => entity.EntityId == command.EntityIds[0]).SkyShell is null:
                throw new ArgumentException("Sky shell update commands require one sky shell and at least one rotation value.", nameof(command));
            case EditorCommandKind.ReorderSkyShell when command.EntityIds.Count != 1
                || command.Transform is not null || command.Text is not null || command.State is not null
                || command.Transforms?.Count > 0 || command.LevelSettings is not null || command.Points is not null
                || command.Placement is not null || command.DestinationOrder is null or < 0
                || workspace.Content.Entities.Single(entity => entity.EntityId == command.EntityIds[0]).SkyShell is null:
                throw new ArgumentException("Sky shell reorder commands require one sky shell and a destination order.", nameof(command));
            case EditorCommandKind.RemoveInstancedCollisionProxy when command.EntityIds.Count != 1
                || command.Transform is not null || command.Text is not null || command.State is not null
                || command.Transforms?.Count > 0
                || !workspace.Content.Entities.Single(entity => entity.EntityId == command.EntityIds[0])
                    .Asset.IsInstancedCollisionSource():
                throw new ArgumentException("Collision remove commands require one TIE or shrub entity.", nameof(command));
            case EditorCommandKind.SetInstancedCollisionEnabled when command.EntityIds.Count == 0
                || command.Transform is not null || command.Text is not null || command.State is not null
                || command.Transforms?.Count > 0
                || workspace.GetEntities(command.EntityIds).Any(entity =>
                    !entity.Asset.IsInstancedCollisionSource()):
                throw new ArgumentException("Collision toggle commands require TIE or shrub entities.", nameof(command));
            case EditorCommandKind.SetInstancedCollisionRawType when command.EntityIds.Count != 1
                || command.Transform is not null || command.Text is not null || command.State is not null
                || command.Transforms?.Count > 0 || command.InstancedCollisionRawType is null
                || !HasSelectedInstancedCollisionBinding(workspace, command.EntityIds[0]):
                throw new ArgumentException("Collision ID commands require one TIE or shrub with a collision proxy.", nameof(command));
            case EditorCommandKind.SetInstancedCollisionFaceTypes when command.EntityIds.Count != 1
                || command.Transform is not null || command.Text is not null || command.State is not null
                || command.Transforms?.Count > 0 || command.InstancedCollisionProxyAssetId is null
                || command.InstancedCollisionFaceTypes is not { Count: > 0 }
                || command.InstancedCollisionFaceTypes.Count > ForgeProjectValidation.MaxInstancedCollisionFaceTypeOverrides
                || command.InstancedCollisionFaceTypes.Any(value => value is null || value.FaceIndex < 0)
                || command.InstancedCollisionFaceTypes.Select(value => value.FaceIndex).Distinct().Count()
                    != command.InstancedCollisionFaceTypes.Count
                || !HasSelectedInstancedCollisionBinding(
                    workspace, command.EntityIds[0], command.InstancedCollisionProxyAssetId):
                throw new ArgumentException(
                    "Collision face commands require one TIE or shrub, its current proxy, and unique face assignments.",
                    nameof(command));
            case EditorCommandKind.SetEntityReference when command.EntityIds.Count != 1
                || command.Transform is not null || command.Text is not null || command.State is not null
                || command.Transforms?.Count > 0
                || command.ReferenceUpdate is not { } reference
                || reference.FieldKey.Length is 0 or > 128
                || reference.FieldKey.Any(character =>
                    !(char.IsAsciiLetterOrDigit(character) || character is '.' or '[' or ']' or '-'))
                || reference.SourceValue is < 0
                || reference.TargetEntityId is { } target && !known.Contains(target):
                throw new ArgumentException(
                    "Reference commands require one owner, a valid field key, and a known target.",
                    nameof(command));
            case EditorCommandKind.ReplaceHudTexture when command.EntityIds.Count != 0
                || HasNonHudCommandData(command)
                || command.HudEdit is not
                {
                    SourceAssetId: not null,
                    SpriteId: null,
                    BankIndex: null,
                    ImageFormat: not null,
                    ImageBytes.Length: > 0,
                }:
                throw new ArgumentException(
                    "HUD replacement commands require a source texture and image.", nameof(command));
            case EditorCommandKind.RemoveHudTextureOverride when command.EntityIds.Count != 0
                || HasNonHudCommandData(command)
                || command.HudEdit is not
                {
                    SourceAssetId: not null,
                    SpriteId: null,
                    BankIndex: null,
                    ImageFormat: null,
                    ImageBytes: null,
                }:
                throw new ArgumentException(
                    "HUD override removal commands require only a source texture.", nameof(command));
            case EditorCommandKind.AddHudIcon when command.EntityIds.Count != 0
                || HasNonHudCommandData(command)
                || command.HudEdit is not
                {
                    SourceAssetId: null,
                    SpriteId: not null,
                    BankIndex: >= 0,
                    ImageFormat: not null,
                    ImageBytes.Length: > 0,
                }:
                throw new ArgumentException(
                    "HUD add commands require a sprite ID, bank, and image.", nameof(command));
            case EditorCommandKind.RemoveHudIcon when command.EntityIds.Count != 0
                || HasNonHudCommandData(command)
                || command.HudEdit is not
                {
                    SourceAssetId: null,
                    SpriteId: not null,
                    BankIndex: null,
                    ImageFormat: null,
                    ImageBytes: null,
                }:
                throw new ArgumentException(
                    "HUD removal commands require only a sprite ID.", nameof(command));
            case EditorCommandKind.ReplaceFxTexture when command.EntityIds.Count != 0
                || HasNonFxCommandData(command)
                || command.FxEdit is not
                {
                    SourceAssetId: not null,
                    Index: null,
                    ImageFormat: not null,
                    ImageBytes.Length: > 0,
                }:
                throw new ArgumentException(
                    "FX replacement commands require a source texture and image.", nameof(command));
            case EditorCommandKind.RemoveFxTextureOverride when command.EntityIds.Count != 0
                || HasNonFxCommandData(command)
                || command.FxEdit is not
                {
                    SourceAssetId: not null,
                    Index: null,
                    ImageFormat: null,
                    ImageBytes: null,
                }:
                throw new ArgumentException(
                    "FX override removal commands require only a source texture.", nameof(command));
            case EditorCommandKind.AddFxTexture when command.EntityIds.Count != 0
                || HasNonFxCommandData(command)
                || command.FxEdit is not
                {
                    SourceAssetId: null,
                    Index: null,
                    ImageFormat: not null,
                    ImageBytes.Length: > 0,
                }:
                throw new ArgumentException(
                    "FX add commands require only an image.", nameof(command));
            case EditorCommandKind.RemoveFxTexture when command.EntityIds.Count != 0
                || HasNonFxCommandData(command)
                || command.FxEdit is not
                {
                    SourceAssetId: null,
                    Index: >= 0,
                    ImageFormat: null,
                    ImageBytes: null,
                }:
                throw new ArgumentException(
                    "FX removal commands require only an appended index.", nameof(command));
        }
        if (command.Kind is EditorCommandKind.UpdateTransform or EditorCommandKind.UpdateTransforms)
            ValidateTransformCapabilities(command, workspace);
    }

    private static bool HasNonHudCommandData(EditorCommand command) =>
        command.Transform is not null || command.Text is not null || command.State is not null
        || command.Transforms?.Count > 0 || command.LevelSettings is not null || command.Points is not null
        || command.Placement is not null || command.SkyShellSource is not null || command.SkyShellUpdate is not null
        || command.DestinationOrder is not null || command.InstancedCollisionEnabled is not null
        || command.InstancedCollisionRawType is not null || command.InstancedCollisionProxyAssetId is not null
        || command.InstancedCollisionFaceTypes is not null || command.ReferenceUpdate is not null
        || command.PaletteOptimization is not null || command.FxEdit is not null;

    private static bool HasNonFxCommandData(EditorCommand command) =>
        command.Transform is not null || command.Text is not null || command.State is not null
        || command.Transforms?.Count > 0 || command.LevelSettings is not null || command.Points is not null
        || command.Placement is not null || command.SkyShellSource is not null || command.SkyShellUpdate is not null
        || command.DestinationOrder is not null || command.InstancedCollisionEnabled is not null
        || command.InstancedCollisionRawType is not null || command.InstancedCollisionProxyAssetId is not null
        || command.InstancedCollisionFaceTypes is not null || command.ReferenceUpdate is not null
        || command.PaletteOptimization is not null || command.HudEdit is not null;

    private static void ValidateCommandId(string commandId)
    {
        if (!Guid.TryParseExact(commandId, "D", out var parsed) || parsed == Guid.Empty
            || parsed.ToString("D") != commandId)
            throw new ArgumentException("Command ID must be a lowercase canonical UUID.", nameof(commandId));
    }

    private static bool HasSelectedInstancedCollisionBinding(
        ForgeProjectWorkspace workspace,
        EntityId entityId,
        AssetId? proxyAssetId = null)
    {
        var entity = workspace.GetEntities([entityId])[0];
        if (!entity.Asset.IsInstancedCollisionSource()) return false;
        var instanceEntityId = entity.InstancedCollisionEnabled == false ? entityId : (EntityId?)null;
        return workspace.Content.InstancedCollisionBindings.Any(binding =>
            binding.SourceAssetId == entity.Asset.Id
            && binding.InstanceEntityId == instanceEntityId
            && (proxyAssetId is null || binding.ProxyAssetId == proxyAssetId));
    }

    private static bool IsEditablePath(ProjectEntity entity) =>
        entity.Geometry?.Spline is not null || entity.Geometry?.GrindPath is not null;

    private void ValidateTransformCapabilities(EditorCommand command, ForgeProjectWorkspace workspace)
    {
        var entities = workspace.Content.Entities.ToDictionary(entity => entity.EntityId);
        var updates = command.Kind == EditorCommandKind.UpdateTransform
            ? [new EditorTransformUpdate(command.EntityIds[0], command.Transform!)]
            : command.Transforms!;
        foreach (var update in updates)
        {
            var entity = entities[update.EntityId];
            var capabilities = TransformCapabilities(entity);
            if (!capabilities.HasFlag(EditorTransformCapabilities.Translate)
                    && update.Transform.Position != entity.Transform.Position
                || !capabilities.HasFlag(EditorTransformCapabilities.Rotate)
                    && update.Transform.Rotation != entity.Transform.Rotation
                || !capabilities.HasFlag(EditorTransformCapabilities.Scale)
                    && update.Transform.Scale != entity.Transform.Scale)
                throw new ArgumentException("Transform command changes an unsupported component.", nameof(command));
        }
    }

    private static ProjectTransform EffectiveTransform(ProjectEntity entity, ProjectTransform transform)
    {
        if (entity.Collision is not { } collision) return transform;
        var horizontalPrecision = collision.Kind == ProjectCollisionPieceKind.Solid ? 16 : 64;
        return transform with
        {
            Position = new(
                Quantize(transform.Position.X, horizontalPrecision),
                Quantize(transform.Position.Y, horizontalPrecision),
                Quantize(transform.Position.Z, 64)),
        };
    }

    private static float Quantize(float value, int precision) => MathF.Round(value * precision) / precision;

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

    private async Task ValidateSkyMutationAsync(
        ForgeProjectWorkspace workspace,
        EditorCommand command,
        ForgeProjectState stateBefore,
        EntityId[] selectionBefore,
        CancellationToken cancellationToken)
    {
        try
        {
            if (_skyShellCommandExecutor is null || _catalogRootPath is null)
                throw new InvalidOperationException("Sky shell editing is unavailable.");
            await _skyShellCommandExecutor(workspace, _catalogRootPath, command, cancellationToken);
        }
        catch
        {
            workspace.RestoreState(stateBefore);
            _selection = selectionBefore;
            throw;
        }
    }

    private static long EstimateHistoryBytes(EditorCommand command, ForgeProjectState before) =>
        256L + (long)before.Content.Entities.Count * IntPtr.Size
        + (long)command.EntityIds.Count * 16
        + (long)(command.Transforms?.Count ?? 0) * 64
        + (long)(command.Points?.Count ?? 0) * 16
        + (long)(command.InstancedCollisionFaceTypes?.Count ?? 0) * 8
        + (command.HudEdit?.ImageBytes?.LongLength ?? 0)
        + (command.FxEdit?.ImageBytes?.LongLength ?? 0)
        + (command.Text?.Length ?? 0) * sizeof(char);

    private static HashSet<AssetId> FindMissingAssets(ForgeProjectWorkspace workspace, AssetCatalogStore catalog) =>
        workspace.Content.Entities
            .Where(entity => entity.Asset is not null)
            .GroupBy(entity => entity.Asset!)
            .Where(group => workspace.ResolveAsset(group.Key, catalog) is null)
            .Select(group => group.Key.Id)
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
