namespace Forge.Host.Domain;

public sealed partial class EditorRuntime
{
    private readonly EditorOcclusionCoveragePreviewExecutor? _occlusionCoveragePreviewExecutor;
    private readonly SemaphoreSlim _coveragePreviewGate = new(1, 1);

    internal async Task<EditorOcclusionCoveragePreview> PreviewOcclusionCoverageAsync(
        string expectedProjectFingerprint, string sourcePath, Func<IsoProgress, ValueTask>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (_occlusionCoveragePreviewExecutor is null)
            throw new InvalidOperationException("Occlusion coverage preview is unavailable in this build.");
        if (!await _coveragePreviewGate.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("An occlusion coverage preview is already running. Cancel it or wait for completion.");
        try
        {
            ForgeProjectWorkspace original, projection; string recipe; string? catalogRoot; long version;
            await _gate.WaitAsync(cancellationToken);
            try
            {
                ThrowIfDisposed(); original = RequireWorkspace();
                if (original.CurrentFingerprint != expectedProjectFingerprint)
                    throw new InvalidOperationException("The project changed. Refresh coverage before previewing it.");
                projection = original.CreateProjection(original.Content with
                {
                    OcclusionCoverage = Occlusion.OcclusionCoverageRecipe.Copy(original.Content.OcclusionCoverage),
                });
                recipe = original.OcclusionCoverageRecipeFingerprint; version = _projectVersion; catalogRoot = _catalogRootPath;
            }
            finally { _gate.Release(); }
            var evaluation = await Task.Run(() => _occlusionCoveragePreviewExecutor(
                projection, catalogRoot, sourcePath, progress, cancellationToken), cancellationToken);
            await _gate.WaitAsync(cancellationToken);
            try
            {
                ThrowIfDisposed(); cancellationToken.ThrowIfCancellationRequested();
                if (!ReferenceEquals(original, _workspace) || version != _projectVersion
                    || original.CurrentFingerprint != expectedProjectFingerprint)
                    throw new InvalidOperationException("The project changed while previewing coverage. Preview again.");
                return new(expectedProjectFingerprint, recipe, evaluation);
            }
            finally { _gate.Release(); }
        }
        finally { _coveragePreviewGate.Release(); }
    }

    internal Task<EditorOcclusionCoverageSnapshot> QueryOcclusionCoverageAsync(CancellationToken cancellationToken = default) =>
        ReadWorkspaceAsync((workspace, _) => Task.FromResult(CoverageSnapshot(workspace)), cancellationToken);

    private static EditorOcclusionCoverageSnapshot CoverageSnapshot(ForgeProjectWorkspace workspace) => new(
        workspace.CurrentFingerprint, workspace.OcclusionCoverageRecipeFingerprint,
        workspace.OcclusionVisibilityFingerprint,
        Occlusion.OcclusionCoverageRecipe.Copy(workspace.Content.OcclusionCoverage));

    internal async Task<EditorOcclusionCoverageUpdateResult> UpdateOcclusionCoverageAsync(
        string expectedProjectFingerprint,
        ProjectOcclusionCoverageState coverage,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedProjectFingerprint);
        ArgumentNullException.ThrowIfNull(coverage);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            var workspace = RequireWorkspace();
            if (expectedProjectFingerprint != workspace.CurrentFingerprint)
                throw new InvalidOperationException("The occlusion coverage edit no longer matches the active project. Refresh the coverage and retry.");
            var before = workspace.CaptureState();
            workspace.SetOcclusionCoverage(coverage, cancellationToken);
            if (workspace.CurrentFingerprint == expectedProjectFingerprint) return new(Snapshot(), CoverageSnapshot(workspace));
            _history.Push(before, workspace.CaptureState(), _selection, _selection, [],
                ForgeProjectPersistence.Serialize(before.Content.OcclusionCoverage).LongLength
                + ForgeProjectPersistence.Serialize(workspace.Content.OcclusionCoverage).LongLength);
            _projectVersion++;
            _groupTemplatePreviews = [];
            AddEvent(EditorEventKind.ProjectChanged, null, [], "Occlusion coverage updated");
            ScheduleAutosave();
            return new(Snapshot(), CoverageSnapshot(workspace));
        }
        finally { _gate.Release(); }
    }
}
