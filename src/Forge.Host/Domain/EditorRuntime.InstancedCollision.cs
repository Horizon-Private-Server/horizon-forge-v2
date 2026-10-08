namespace Forge.Host.Domain;

public sealed partial class EditorRuntime
{
    private readonly EditorInstancedCollisionPreviewExecutor? _instancedCollisionPreviewExecutor;
    private readonly EditorInstancedCollisionSourceInspector? _instancedCollisionSourceInspector;
    private readonly EditorInstancedCollisionFaceCountResolver? _instancedCollisionFaceCountResolver;
    private Dictionary<(AssetId, EntityId?), ProjectInstancedCollisionBinding> _savedInstancedCollisionBindings = [];
    private Dictionary<string, CachedInstancedCollisionCandidate> _instancedCollisionCandidates = [];

    private sealed record CachedInstancedCollisionCandidate(
        EntityId ProjectId,
        EntityId EntityId,
        AssetId SourceAssetId,
        long ProjectVersion,
        EditorInstancedCollisionCandidate Candidate);

    public async Task<EditorSnapshot> ApplyInstancedCollisionProxyAsync(
        string commandId,
        AssetId sourceAssetId,
        ReadOnlyMemory<byte> canonicalBytes,
        uint canonicalFormatVersion,
        ProjectInstancedCollisionRecipe recipe,
        CancellationToken cancellationToken = default)
    {
        ValidateCommandId(commandId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            var workspace = RequireWorkspace();
            var before = workspace.CaptureState();
            var fingerprintBefore = workspace.CurrentFingerprint;
            await workspace.ApplyInstancedCollisionProxyAsync(
                sourceAssetId, canonicalBytes, canonicalFormatVersion, recipe, cancellationToken);
            var entityIds = workspace.Content.Entities
                .Where(entity => entity.Asset is { } asset && asset.IsInstancedCollisionSource()
                    && asset.Id == sourceAssetId)
                .Select(entity => entity.EntityId)
                .ToArray();
            if (workspace.CurrentFingerprint != fingerprintBefore)
            {
                _projectVersion++;
                _history.Push(before, workspace.CaptureState(), _selection, _selection, entityIds, 512);
            }
            AddEvent(EditorEventKind.ProjectChanged, commandId, entityIds, "Instanced collision proxy applied");
            _instancedCollisionCandidates = [];
            ScheduleAutosave();
            return Snapshot();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<EditorInstancedCollisionPreview> PreviewInstancedCollisionAsync(
        EntityId entityId,
        EditorInstancedCollisionGenerationSettings? settings = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            var workspace = RequireWorkspace();
            if (_instancedCollisionPreviewExecutor is null || _catalogRootPath is null)
                throw new InvalidOperationException("Instanced collision preview is unavailable.");
            var entity = workspace.Content.Entities.SingleOrDefault(value => value.EntityId == entityId)
                ?? throw new ArgumentException("Instanced collision preview entity is not present in the project.", nameof(entityId));
            var sourceAssetId = entity.Asset is { } asset && asset.IsInstancedCollisionSource()
                ? asset.Id
                : throw new ArgumentException("Collision preview requires a TIE or shrub entity.", nameof(entityId));
            var generated = await _instancedCollisionPreviewExecutor(
                workspace, _catalogRootPath, entityId, sourceAssetId, settings, cancellationToken);
            if (generated.Count is 0 or > 16)
                throw new InvalidDataException("Instanced collision preview returned an invalid candidate count.");

            var cache = new Dictionary<string, CachedInstancedCollisionCandidate>(StringComparer.Ordinal);
            var snapshots = new List<EditorInstancedCollisionCandidateSnapshot>(generated.Count);
            foreach (var candidate in generated)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (candidate.CanonicalBytes.Length == 0 || candidate.Octants.Count > 1_000_000)
                    throw new InvalidDataException("Instanced collision preview returned invalid candidate data.");
                ForgeProjectValidation.ValidateInstancedCollisionRecipe(candidate.Recipe);
                var token = Guid.NewGuid().ToString("D");
                cache.Add(token, new(
                    workspace.Manifest.ProjectId, entityId, sourceAssetId, _projectVersion, candidate));
                snapshots.Add(new(
                    token,
                    candidate.Preset,
                    candidate.Label,
                    candidate.Recipe,
                    candidate.CanonicalBytes.Length,
                    candidate.VertexCount,
                    candidate.FaceCount,
                    candidate.OccupiedOctantCount,
                    candidate.DuplicateFaceCount,
                    candidate.HardViolationCount,
                    candidate.MaximumDeviation,
                    candidate.DeviationSampleCount,
                    candidate.Octants,
                    candidate.CombinedAnalysis));
            }

            _instancedCollisionCandidates = cache;
            return new(sourceAssetId, snapshots);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<EditorInstancedCollisionSourceInfo> InspectInstancedCollisionSourceAsync(
        EntityId entityId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            var workspace = RequireWorkspace();
            if (_instancedCollisionSourceInspector is null || _catalogRootPath is null)
                throw new InvalidOperationException("Instanced collision source inspection is unavailable.");
            var entity = workspace.Content.Entities.SingleOrDefault(value => value.EntityId == entityId)
                ?? throw new ArgumentException("Instanced collision entity is not present in the project.", nameof(entityId));
            var sourceAssetId = entity.Asset is { } asset && asset.IsInstancedCollisionSource()
                ? asset.Id
                : throw new ArgumentException("Collision source inspection requires a TIE or shrub entity.", nameof(entityId));
            return await _instancedCollisionSourceInspector(
                workspace, _catalogRootPath, sourceAssetId, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<EditorSnapshot> ApplyInstancedCollisionPreviewAsync(
        string commandId,
        string token,
        CancellationToken cancellationToken = default)
    {
        ValidateCommandId(commandId);
        if (!Guid.TryParse(token, out _))
            throw new ArgumentException("Instanced collision preview token is invalid.", nameof(token));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            var workspace = RequireWorkspace();
            if (!_instancedCollisionCandidates.TryGetValue(token, out var cached)
                || cached.ProjectId != workspace.Manifest.ProjectId)
                throw new InvalidOperationException("Instanced collision preview is stale; generate it again.");
            if (cached.ProjectVersion != _projectVersion)
                throw new InvalidOperationException("The project changed after this preview; generate it again.");
            if (!workspace.Content.Entities.Any(entity => entity.Asset is { } asset && asset.IsInstancedCollisionSource()
                && entity.EntityId == cached.EntityId && asset.Id == cached.SourceAssetId))
                throw new InvalidOperationException("The previewed asset is no longer present in the project.");
            if (cached.Candidate.HardViolationCount > 0
                || cached.Candidate.CombinedAnalysis is { HardViolationCount: > 0 }
                || cached.Candidate.CombinedAnalysis?.Error is not null)
                throw new InvalidOperationException("Unsafe instanced collision candidates cannot be applied.");

            var before = workspace.CaptureState();
            var fingerprintBefore = workspace.CurrentFingerprint;
            var entity = workspace.Content.Entities.Single(value => value.EntityId == cached.EntityId);
            var individual = entity.InstancedCollisionEnabled == false;
            var hadBinding = workspace.Content.InstancedCollisionBindings.Any(binding =>
                binding.SourceAssetId == cached.SourceAssetId
                && binding.InstanceEntityId == (individual ? cached.EntityId : null));
            if (individual)
                await workspace.ApplyInstancedCollisionProxyAsync(
                    cached.EntityId, cached.Candidate.CanonicalBytes, 0, cached.Candidate.Recipe, cancellationToken);
            else
                await workspace.ApplyInstancedCollisionProxyAsync(
                    cached.SourceAssetId, cached.Candidate.CanonicalBytes, 0, cached.Candidate.Recipe, cancellationToken);
            if (!hadBinding && !individual) workspace.SetInstancedCollisionEnabled(cached.EntityId, true);
            var entityIds = individual
                ? [cached.EntityId]
                : workspace.Content.Entities
                    .Where(value => value.Asset is { } asset && asset.IsInstancedCollisionSource()
                        && asset.Id == cached.SourceAssetId)
                    .Select(value => value.EntityId)
                    .ToArray();
            if (workspace.CurrentFingerprint != fingerprintBefore)
            {
                _projectVersion++;
                _history.Push(before, workspace.CaptureState(), _selection, _selection, entityIds, 512);
            }
            AddEvent(EditorEventKind.ProjectChanged, commandId, entityIds, "Instanced collision proxy applied");
            _instancedCollisionCandidates = new(StringComparer.Ordinal)
            {
                [token] = cached with { ProjectVersion = _projectVersion },
            };
            ScheduleAutosave();
            return Snapshot();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<EditorInstancedCollisionCandidate> GetInstancedCollisionPreviewCandidateAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(token, out _))
            throw new ArgumentException("Instanced collision preview token is invalid.", nameof(token));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            var workspace = RequireWorkspace();
            if (!_instancedCollisionCandidates.TryGetValue(token, out var cached)
                || cached.ProjectId != workspace.Manifest.ProjectId)
                throw new InvalidOperationException("Instanced collision preview is stale; generate it again.");
            if (cached.ProjectVersion != _projectVersion)
                throw new InvalidOperationException("The project changed after this preview; generate it again.");
            return cached.Candidate;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<byte[]> ReadAppliedInstancedCollisionProxyAsync(
        AssetId proxyAssetId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            var workspace = RequireWorkspace();
            if (!workspace.Content.InstancedCollisionBindings.Any(value => value.ProxyAssetId == proxyAssetId))
                throw new InvalidOperationException("The applied instanced collision proxy is stale or no longer bound.");
            var proxy = workspace.Content.Assets.Single(value => value.Id == proxyAssetId);
            var path = workspace.ResolveAttachedAssetPath(proxyAssetId)
                ?? throw new FileNotFoundException($"Applied instanced collision proxy {proxyAssetId} is missing.");
            return await AssetCatalogBlobReader.ReadVerifiedAsync(
                proxy.Id,
                proxy.Kind,
                proxy.CanonicalFormatVersion,
                proxy.Size,
                path,
                ForgeProjectWorkspace.MaxRecoveryBytes,
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<EntityId>> ExecuteInstancedCollisionCommandAsync(
        ForgeProjectWorkspace workspace,
        EditorCommand command,
        CancellationToken cancellationToken)
    {
        var entity = workspace.GetEntities(command.EntityIds)[0];
        IReadOnlyList<EntityId> affectedEntityIds = entity.InstancedCollisionEnabled == false
            ? [entity.EntityId]
            : workspace.Content.Entities
                .Where(value => value.Asset is { } asset && asset.IsInstancedCollisionSource()
                    && asset.Id == entity.Asset!.Id)
                .Select(value => value.EntityId)
                .ToArray();

        switch (command.Kind)
        {
            case EditorCommandKind.RemoveInstancedCollisionProxy:
                workspace.RemoveInstancedCollisionProxy(entity.EntityId);
                AddEvent(EditorEventKind.ProjectChanged, command.Id, affectedEntityIds,
                    "Instanced collision proxy removed");
                break;
            case EditorCommandKind.SetInstancedCollisionEnabled:
                workspace.SetInstancedCollisionEnabled(command.EntityIds, command.InstancedCollisionEnabled);
                affectedEntityIds = command.EntityIds;
                AddEvent(EditorEventKind.ProjectChanged, command.Id, affectedEntityIds,
                    command.InstancedCollisionEnabled switch
                    {
                        true => "Shared instanced collision enabled",
                        false => "Individual instanced collision enabled",
                        null => "Instanced collision disabled",
                    });
                break;
            case EditorCommandKind.SetInstancedCollisionRawType:
                workspace.SetInstancedCollisionRawType(entity.EntityId, command.InstancedCollisionRawType!.Value);
                AddEvent(EditorEventKind.ProjectChanged, command.Id, affectedEntityIds,
                    "Instanced collision IDs updated");
                break;
            case EditorCommandKind.SetInstancedCollisionFaceTypes:
                if (_instancedCollisionFaceCountResolver is null || _catalogRootPath is null)
                    throw new InvalidOperationException("Instanced collision face painting is unavailable.");
                var faceCount = await _instancedCollisionFaceCountResolver(
                    workspace,
                    _catalogRootPath,
                    command.InstancedCollisionProxyAssetId!.Value,
                    cancellationToken);
                workspace.SetInstancedCollisionFaceTypes(
                    entity.EntityId,
                    command.InstancedCollisionProxyAssetId.Value,
                    command.InstancedCollisionFaceTypes!,
                    faceCount);
                AddEvent(EditorEventKind.ProjectChanged, command.Id, affectedEntityIds,
                    "Instanced collision face IDs updated");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command), command.Kind,
                    "Unknown instanced collision command");
        }
        ScheduleAutosave();
        return affectedEntityIds;
    }

    private static async Task<EditorDiagnostic[]> InspectInstancedCollisionProxiesAsync(
        ForgeProjectWorkspace workspace,
        AssetCatalogStore? catalog,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<EditorDiagnostic>();
        foreach (var binding in workspace.Content.InstancedCollisionBindings
            .OrderBy(binding => binding.SourceAssetId.ToString(), StringComparer.Ordinal)
            .ThenBy(binding => binding.InstanceEntityId?.ToString(), StringComparer.Ordinal))
        {
            var proxy = workspace.Content.Assets.Single(asset => asset.Id == binding.ProxyAssetId);
            var path = workspace.ResolveAttachedAssetPath(proxy.Id)
                ?? catalog?.ResolveBlobPath(proxy.Id);
            if (path is null)
            {
                diagnostics.Add(MissingInstancedCollisionProxyDiagnostic(binding));
                continue;
            }

            try
            {
                _ = await AssetCatalogBlobReader.ReadVerifiedAsync(
                    proxy.Id,
                    proxy.Kind,
                    proxy.CanonicalFormatVersion,
                    proxy.Size,
                    path,
                    ForgeProjectWorkspace.MaxRecoveryBytes,
                    cancellationToken);
            }
            catch (FileNotFoundException)
            {
                diagnostics.Add(MissingInstancedCollisionProxyDiagnostic(binding));
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(new(
                    "instanced-collision.proxy-corrupt",
                    EditorDiagnosticSeverity.Error,
                    $"Collision proxy {binding.ProxyAssetId} for source asset {binding.SourceAssetId} failed integrity verification ({exception.Message}). Restore it from backup or regenerate it before baking."));
            }
        }
        return diagnostics.ToArray();
    }

    private static EditorDiagnostic MissingInstancedCollisionProxyDiagnostic(ProjectInstancedCollisionBinding binding) =>
        new(
            "instanced-collision.proxy-missing",
            EditorDiagnosticSeverity.Error,
            $"Collision proxy {binding.ProxyAssetId} for source asset {binding.SourceAssetId} is missing. Restore the project assets folder or regenerate the proxy before baking.");
}
