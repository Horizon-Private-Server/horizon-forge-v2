namespace Forge.Host.Domain;

public sealed record EditorOcclusionCoverageSnapshot(
    string ProjectFingerprint,
    string RecipeFingerprint,
    string VisibilityFingerprint,
    ProjectOcclusionCoverageState State);

public sealed record EditorOcclusionCoverageUpdateResult(
    EditorSnapshot Project,
    EditorOcclusionCoverageSnapshot Coverage);

public sealed record EditorOcclusionCoverageCell(int X, int Y, int Z);
public sealed record EditorOcclusionCoverageSourceCount(EntityId Id, int Cells);
public sealed record EditorOcclusionCoverageEvaluation(
    int AlgorithmVersion, float CellSize, string PrimaryFingerprint, string CoordinateFingerprint,
    int PrimaryCells, int CandidateTests, IReadOnlyList<EditorOcclusionCoverageCell> Cells,
    IReadOnlyList<EditorOcclusionCoverageSourceCount> Sources, EditorOcclusionCoverageGraphPreview? GraphPreview = null);
public sealed record EditorOcclusionCoverageGraphResult(EntityId Id, IReadOnlyList<EditorOcclusionCoverageCell> Accepted,
    IReadOnlyList<EditorOcclusionCoverageCell> Rejected);
public sealed record EditorOcclusionCoverageGraphPreview(string SceneFingerprint, int Rays, int QueryTests,
    IReadOnlyList<EditorOcclusionCoverageGraphResult> Sources);
public sealed record EditorOcclusionCoveragePreview(
    string ProjectFingerprint, string RecipeFingerprint, EditorOcclusionCoverageEvaluation Evaluation);

public delegate Task<EditorOcclusionCoverageEvaluation> EditorOcclusionCoveragePreviewExecutor(
    ForgeProjectWorkspace projection, string? catalogRoot, string sourcePath, Func<IsoProgress, ValueTask>? progress,
    CancellationToken cancellationToken);
