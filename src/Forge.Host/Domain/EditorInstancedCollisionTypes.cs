namespace Forge.Host.Domain;

public enum EditorInstancedCollisionPreset : byte
{
    Surface = 1,
    SolidHull = 3,
}

public sealed record EditorInstancedCollisionGenerationSettings(
    byte RawType = 0x0f,
    int ProfileSections = 6,
    int SurfaceLodIndex = -1,
    bool UseHull = false);

public sealed record EditorCollisionOctantCost(
    int X,
    int Y,
    int Z,
    int FaceCount,
    int VertexCount,
    int QuadCount,
    int EncodedByteCount,
    IReadOnlyList<string> Violations,
    IReadOnlyList<string>? AdditionIds = null);

public sealed record EditorInstancedCollisionCombinedAnalysis(
    int InstanceCount,
    int LogicalFaceCount,
    int OccupiedOctantCount,
    int DuplicateFaceCount,
    int HardViolationCount,
    IReadOnlyList<EditorCollisionOctantCost> Octants,
    string? Error = null);

public sealed record EditorInstancedCollisionCandidate(
    EditorInstancedCollisionPreset Preset,
    string Label,
    ProjectInstancedCollisionRecipe Recipe,
    byte[] CanonicalBytes,
    int VertexCount,
    int FaceCount,
    int OccupiedOctantCount,
    int DuplicateFaceCount,
    int HardViolationCount,
    float MaximumDeviation,
    int DeviationSampleCount,
    IReadOnlyList<EditorCollisionOctantCost> Octants,
    EditorInstancedCollisionCombinedAnalysis? CombinedAnalysis = null);

public delegate Task<IReadOnlyList<EditorInstancedCollisionCandidate>> EditorInstancedCollisionPreviewExecutor(
    ForgeProjectWorkspace workspace,
    string catalogRootPath,
    EntityId entityId,
    AssetId sourceAssetId,
    EditorInstancedCollisionGenerationSettings? settings,
    CancellationToken cancellationToken);

public sealed record EditorInstancedCollisionSourceInfo(
    AssetId SourceAssetId,
    IReadOnlyList<int> SurfaceLodIndices);

public delegate Task<EditorInstancedCollisionSourceInfo> EditorInstancedCollisionSourceInspector(
    ForgeProjectWorkspace workspace,
    string catalogRootPath,
    AssetId sourceAssetId,
    CancellationToken cancellationToken);

public delegate Task<int> EditorInstancedCollisionFaceCountResolver(
    ForgeProjectWorkspace workspace,
    string catalogRootPath,
    AssetId proxyAssetId,
    CancellationToken cancellationToken);

public sealed record EditorInstancedCollisionCandidateSnapshot(
    string Token,
    EditorInstancedCollisionPreset Preset,
    string Label,
    ProjectInstancedCollisionRecipe Recipe,
    int EncodedByteCount,
    int VertexCount,
    int FaceCount,
    int OccupiedOctantCount,
    int DuplicateFaceCount,
    int HardViolationCount,
    float MaximumDeviation,
    int DeviationSampleCount,
    IReadOnlyList<EditorCollisionOctantCost> Octants,
    EditorInstancedCollisionCombinedAnalysis? CombinedAnalysis = null);

public sealed record EditorInstancedCollisionPreview(
    AssetId SourceAssetId,
    IReadOnlyList<EditorInstancedCollisionCandidateSnapshot> Candidates);

public sealed record EditorInstancedCollisionBindingSnapshot(
    AssetId ProxyAssetId,
    ProjectInstancedCollisionRecipe Recipe,
    IReadOnlyList<ProjectCollisionFaceTypeOverride> FaceTypeOverrides);
