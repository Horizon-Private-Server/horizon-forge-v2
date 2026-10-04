namespace Forge.Host.Domain;

public sealed partial class EditorRuntime
{
    private readonly EditorTieCollisionPreviewExecutor? _tieCollisionPreviewExecutor;
    private readonly EditorTieCollisionSourceInspector? _tieCollisionSourceInspector;
    private readonly EditorTieCollisionFaceCountResolver? _tieCollisionFaceCountResolver;
    private Dictionary<AssetId, ProjectTieCollisionBinding> _savedTieCollisionBindings = [];
    private Dictionary<string, CachedTieCollisionCandidate> _tieCollisionCandidates = [];

    private sealed record CachedTieCollisionCandidate(
        EntityId ProjectId,
        AssetId TieAssetId,
        long ProjectVersion,
        EditorTieCollisionCandidate Candidate);

    public async Task<EditorSnapshot> ApplyTieCollisionProxyAsync(
        string commandId,
        AssetId tieAssetId,
        ReadOnlyMemory<byte> canonicalBytes,
        uint canonicalFormatVersion,
        ProjectTieCollisionRecipe recipe,
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
            await workspace.ApplyTieCollisionProxyAsync(
                tieAssetId, canonicalBytes, canonicalFormatVersion, recipe, cancellationToken);
            var entityIds = workspace.Content.Entities
                .Where(entity => entity.Asset is { Kind: AssetKind.Tie } asset && asset.Id == tieAssetId)
                .Select(entity => entity.EntityId)
                .ToArray();
            if (workspace.CurrentFingerprint != fingerprintBefore)
            {
                _projectVersion++;
                _history.Push(before, workspace.CaptureState(), _selection, _selection, entityIds, 512);
            }
            AddEvent(EditorEventKind.ProjectChanged, commandId, entityIds, "TIE collision proxy applied");
            _tieCollisionCandidates = [];
            ScheduleAutosave();
            return Snapshot();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<EditorTieCollisionPreview> PreviewTieCollisionAsync(
        EntityId entityId,
        EditorTieCollisionGenerationSettings? settings = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            var workspace = RequireWorkspace();
            if (_tieCollisionPreviewExecutor is null || _catalogRootPath is null)
                throw new InvalidOperationException("TIE collision preview is unavailable.");
            var entity = workspace.Content.Entities.SingleOrDefault(value => value.EntityId == entityId)
                ?? throw new ArgumentException("TIE collision preview entity is not present in the project.", nameof(entityId));
            var tieAssetId = entity.Asset is { Kind: AssetKind.Tie } asset
                ? asset.Id
                : throw new ArgumentException("TIE collision preview requires a TIE entity.", nameof(entityId));
            var generated = await _tieCollisionPreviewExecutor(
                workspace, _catalogRootPath, tieAssetId, settings, cancellationToken);
            if (generated.Count is 0 or > 16)
                throw new InvalidDataException("TIE collision preview returned an invalid candidate count.");

            var cache = new Dictionary<string, CachedTieCollisionCandidate>(StringComparer.Ordinal);
            var snapshots = new List<EditorTieCollisionCandidateSnapshot>(generated.Count);
            foreach (var candidate in generated)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (candidate.CanonicalBytes.Length == 0 || candidate.Octants.Count > 1_000_000)
                    throw new InvalidDataException("TIE collision preview returned invalid candidate data.");
                ForgeProjectValidation.ValidateTieCollisionRecipe(candidate.Recipe);
                var token = Guid.NewGuid().ToString("D");
                cache.Add(token, new(
                    workspace.Manifest.ProjectId, tieAssetId, _projectVersion, candidate));
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

            _tieCollisionCandidates = cache;
            return new(tieAssetId, snapshots);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<EditorTieCollisionSourceInfo> InspectTieCollisionSourceAsync(
        EntityId entityId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            var workspace = RequireWorkspace();
            if (_tieCollisionSourceInspector is null || _catalogRootPath is null)
                throw new InvalidOperationException("TIE collision source inspection is unavailable.");
            var entity = workspace.Content.Entities.SingleOrDefault(value => value.EntityId == entityId)
                ?? throw new ArgumentException("TIE collision entity is not present in the project.", nameof(entityId));
            var tieAssetId = entity.Asset is { Kind: AssetKind.Tie } asset
                ? asset.Id
                : throw new ArgumentException("TIE collision source inspection requires a TIE entity.", nameof(entityId));
            return await _tieCollisionSourceInspector(
                workspace, _catalogRootPath, tieAssetId, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<EditorSnapshot> ApplyTieCollisionPreviewAsync(
        string commandId,
        string token,
        CancellationToken cancellationToken = default)
    {
        ValidateCommandId(commandId);
        if (!Guid.TryParse(token, out _))
            throw new ArgumentException("TIE collision preview token is invalid.", nameof(token));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            var workspace = RequireWorkspace();
            if (!_tieCollisionCandidates.TryGetValue(token, out var cached)
                || cached.ProjectId != workspace.Manifest.ProjectId)
                throw new InvalidOperationException("TIE collision preview is stale; generate it again.");
            if (cached.ProjectVersion != _projectVersion)
                throw new InvalidOperationException("The project changed after this preview; generate it again.");
            if (!workspace.Content.Entities.Any(entity => entity.Asset is { Kind: AssetKind.Tie } asset
                && asset.Id == cached.TieAssetId))
                throw new InvalidOperationException("The previewed TIE is no longer present in the project.");
            if (cached.Candidate.HardViolationCount > 0
                || cached.Candidate.CombinedAnalysis is { HardViolationCount: > 0 }
                || cached.Candidate.CombinedAnalysis?.Error is not null)
                throw new InvalidOperationException("Unsafe TIE collision candidates cannot be applied.");

            var before = workspace.CaptureState();
            var fingerprintBefore = workspace.CurrentFingerprint;
            await workspace.ApplyTieCollisionProxyAsync(
                cached.TieAssetId,
                cached.Candidate.CanonicalBytes,
                canonicalFormatVersion: 0,
                cached.Candidate.Recipe,
                cancellationToken);
            var entityIds = workspace.Content.Entities
                .Where(entity => entity.Asset is { Kind: AssetKind.Tie } asset && asset.Id == cached.TieAssetId)
                .Select(entity => entity.EntityId)
                .ToArray();
            if (workspace.CurrentFingerprint != fingerprintBefore)
            {
                _projectVersion++;
                _history.Push(before, workspace.CaptureState(), _selection, _selection, entityIds, 512);
            }
            AddEvent(EditorEventKind.ProjectChanged, commandId, entityIds, "TIE collision proxy applied");
            _tieCollisionCandidates = new(StringComparer.Ordinal)
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

    public async Task<EditorTieCollisionCandidate> GetTieCollisionPreviewCandidateAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(token, out _))
            throw new ArgumentException("TIE collision preview token is invalid.", nameof(token));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            var workspace = RequireWorkspace();
            if (!_tieCollisionCandidates.TryGetValue(token, out var cached)
                || cached.ProjectId != workspace.Manifest.ProjectId)
                throw new InvalidOperationException("TIE collision preview is stale; generate it again.");
            if (cached.ProjectVersion != _projectVersion)
                throw new InvalidOperationException("The project changed after this preview; generate it again.");
            return cached.Candidate;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<byte[]> ReadAppliedTieCollisionProxyAsync(
        AssetId proxyAssetId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            var workspace = RequireWorkspace();
            if (!workspace.Content.TieCollisionBindings.Any(value => value.ProxyAssetId == proxyAssetId))
                throw new InvalidOperationException("The applied TIE collision proxy is stale or no longer bound.");
            var proxy = workspace.Content.Assets.Single(value => value.Id == proxyAssetId);
            var path = workspace.ResolveAttachedAssetPath(proxyAssetId)
                ?? throw new FileNotFoundException($"Applied TIE collision proxy {proxyAssetId} is missing.");
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

    private static async Task<EditorDiagnostic[]> InspectTieCollisionProxiesAsync(
        ForgeProjectWorkspace workspace,
        AssetCatalogStore? catalog,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<EditorDiagnostic>();
        foreach (var binding in workspace.Content.TieCollisionBindings
            .OrderBy(binding => binding.TieAssetId.ToString(), StringComparer.Ordinal))
        {
            var proxy = workspace.Content.Assets.Single(asset => asset.Id == binding.ProxyAssetId);
            var path = workspace.ResolveAttachedAssetPath(proxy.Id)
                ?? catalog?.ResolveBlobPath(proxy.Id);
            if (path is null)
            {
                diagnostics.Add(MissingTieCollisionProxyDiagnostic(binding));
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
                diagnostics.Add(MissingTieCollisionProxyDiagnostic(binding));
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(new(
                    "tie-collision.proxy-corrupt",
                    EditorDiagnosticSeverity.Error,
                    $"Collision proxy {binding.ProxyAssetId} for TIE asset {binding.TieAssetId} failed integrity verification ({exception.Message}). Restore it from backup or regenerate it before baking."));
            }
        }
        return diagnostics.ToArray();
    }

    private static EditorDiagnostic MissingTieCollisionProxyDiagnostic(ProjectTieCollisionBinding binding) =>
        new(
            "tie-collision.proxy-missing",
            EditorDiagnosticSeverity.Error,
            $"Collision proxy {binding.ProxyAssetId} for TIE asset {binding.TieAssetId} is missing. Restore the project assets folder or regenerate the proxy before baking.");
}
