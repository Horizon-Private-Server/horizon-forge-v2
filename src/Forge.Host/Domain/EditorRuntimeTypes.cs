namespace Forge.Host.Domain;

public enum EditorCommandKind : byte
{
    SetSelection = 1,
    RenameProject = 2,
    UpdateTransform = 3,
    RenameEntity = 4,
    SetEntityLayer = 5,
    SetEntityState = 6,
    UpdateTransforms = 7,
    Undo = 8,
    Redo = 9,
    DeleteEntities = 10,
    DuplicateEntities = 11,
    CopyEntities = 12,
    PasteEntities = 13,
    UpdateLevelSettings = 14,
    UpdateSplinePoints = 15,
    CreateEntityFromAsset = 16,
    AddSkyShellFromAsset = 17,
    UpdateSkyShell = 18,
    ReorderSkyShell = 19,
    RemoveTieCollisionProxy = 20,
    SetTieCollisionEnabled = 21,
    SetTieCollisionRawType = 22,
    SetTieCollisionFaceTypes = 23,
}

public enum EditorEventKind : byte
{
    ProjectOpened = 1,
    ProjectChanged = 2,
    SelectionChanged = 3,
    ProjectSaved = 4,
    RecoveryWritten = 5,
    DiagnosticRaised = 6,
    ProjectClosed = 7,
}

public enum EditorDiagnosticSeverity : byte
{
    Info = 1,
    Warning = 2,
    Error = 3,
}

public sealed record EditorCommand(
    string Id,
    EditorCommandKind Kind,
    IReadOnlyList<EntityId> EntityIds,
    ProjectTransform? Transform = null,
    string? Text = null,
    EditorEntityStateChange? State = null,
    IReadOnlyList<EditorTransformUpdate>? Transforms = null,
    ProjectLevelSettings? LevelSettings = null,
    IReadOnlyList<ProjectVector4>? Points = null,
    EditorAssetPlacement? Placement = null,
    EditorSkyShellSource? SkyShellSource = null,
    EditorSkyShellUpdate? SkyShellUpdate = null,
    int? DestinationOrder = null,
    bool? TieCollisionEnabled = null,
    byte? TieCollisionRawType = null,
    AssetId? TieCollisionProxyAssetId = null,
    IReadOnlyList<ProjectCollisionFaceTypeOverride>? TieCollisionFaceTypes = null);

public sealed record EditorAssetPlacement(
    AssetId AssetId,
    AssetKind Kind,
    int ClassId,
    ProjectTransform Transform);

public delegate Task<ProjectEntity> EditorAssetPlacementResolver(
    ForgeProjectWorkspace workspace,
    string catalogRootPath,
    EditorAssetPlacement placement,
    CancellationToken cancellationToken);

public sealed record EditorSkyShellSource(AssetId AssetId, int ShellIndex);

public sealed record EditorSkyShellUpdate(
    ProjectVector3? InitialRotationRadians = null,
    ProjectVector3? AngularVelocityRadiansPerSecond = null);

public delegate Task<IReadOnlyList<EntityId>> EditorSkyShellCommandExecutor(
    ForgeProjectWorkspace workspace,
    string catalogRootPath,
    EditorCommand command,
    CancellationToken cancellationToken);

public enum EditorTieCollisionPreset : byte
{
    Surface = 1,
    SolidHull = 3,
}

public sealed record EditorTieCollisionGenerationSettings(
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

public sealed record EditorTieCollisionCombinedAnalysis(
    int InstanceCount,
    int LogicalFaceCount,
    int OccupiedOctantCount,
    int DuplicateFaceCount,
    int HardViolationCount,
    IReadOnlyList<EditorCollisionOctantCost> Octants,
    string? Error = null);

public sealed record EditorTieCollisionCandidate(
    EditorTieCollisionPreset Preset,
    string Label,
    ProjectTieCollisionRecipe Recipe,
    byte[] CanonicalBytes,
    int VertexCount,
    int FaceCount,
    int OccupiedOctantCount,
    int DuplicateFaceCount,
    int HardViolationCount,
    float MaximumDeviation,
    int DeviationSampleCount,
    IReadOnlyList<EditorCollisionOctantCost> Octants,
    EditorTieCollisionCombinedAnalysis? CombinedAnalysis = null);

public delegate Task<IReadOnlyList<EditorTieCollisionCandidate>> EditorTieCollisionPreviewExecutor(
    ForgeProjectWorkspace workspace,
    string catalogRootPath,
    AssetId tieAssetId,
    EditorTieCollisionGenerationSettings? settings,
    CancellationToken cancellationToken);

public sealed record EditorTieCollisionSourceInfo(
    AssetId TieAssetId,
    IReadOnlyList<int> SurfaceLodIndices);

public delegate Task<EditorTieCollisionSourceInfo> EditorTieCollisionSourceInspector(
    ForgeProjectWorkspace workspace,
    string catalogRootPath,
    AssetId tieAssetId,
    CancellationToken cancellationToken);

public delegate Task<int> EditorTieCollisionFaceCountResolver(
    ForgeProjectWorkspace workspace,
    string catalogRootPath,
    AssetId proxyAssetId,
    CancellationToken cancellationToken);

public sealed record EditorTieCollisionCandidateSnapshot(
    string Token,
    EditorTieCollisionPreset Preset,
    string Label,
    ProjectTieCollisionRecipe Recipe,
    int EncodedByteCount,
    int VertexCount,
    int FaceCount,
    int OccupiedOctantCount,
    int DuplicateFaceCount,
    int HardViolationCount,
    float MaximumDeviation,
    int DeviationSampleCount,
    IReadOnlyList<EditorCollisionOctantCost> Octants,
    EditorTieCollisionCombinedAnalysis? CombinedAnalysis = null);

public sealed record EditorTieCollisionPreview(
    AssetId TieAssetId,
    IReadOnlyList<EditorTieCollisionCandidateSnapshot> Candidates);

public sealed record EditorTieCollisionBindingSnapshot(
    AssetId ProxyAssetId,
    ProjectTieCollisionRecipe Recipe,
    IReadOnlyList<ProjectCollisionFaceTypeOverride> FaceTypeOverrides);

public sealed record EditorTransformUpdate(EntityId EntityId, ProjectTransform Transform);

public sealed record EditorEntityStateChange(
    bool? Hidden = null,
    bool? Disabled = null,
    bool? Locked = null);

public sealed record EditorEntityStatus(
    bool Dirty,
    bool Hidden,
    bool Disabled,
    bool Locked,
    bool ReadOnly,
    bool Invalid,
    bool MissingAsset);

public enum EditorGeometryKind : byte
{
    Cuboid = 1,
    Spline = 2,
    Area = 3,
    Sphere = 4,
    Cylinder = 5,
    Pill = 6,
    GrindPath = 7,
    DirectionalLight = 8,
    PointLight = 9,
    EnvironmentSample = 10,
    EnvironmentTransition = 11,
    Camera = 12,
    AmbientSound = 13,
}

public sealed record EditorEntityGeometry(
    EditorGeometryKind Kind,
    IReadOnlyList<ProjectVector4> Points);

[Flags]
public enum EditorTransformCapabilities : byte
{
    None = 0,
    Translate = 1,
    Rotate = 2,
    Scale = 4,
}

public delegate EditorTransformCapabilities EditorTransformCapabilityResolver(ProjectEntity entity);

public sealed record EditorEntitySnapshot(
    EntityId EntityId,
    string Name,
    string Layer,
    ProjectTransform Transform,
    ProjectAssetReference? Asset,
    ProjectEntityProvenance? Provenance,
    int? SourceClassId,
    EditorEntityGeometry? Geometry,
    EditorTransformCapabilities TransformCapabilities,
    EditorEntityStatus State,
    ProjectSkyShell? SkyShell,
    ProjectCollisionPiece? Collision,
    EditorTieCollisionBindingSnapshot? TieCollision,
    bool? TieCollisionEnabled);

public sealed record EditorEvent(
    long Sequence,
    long CreatedUnixMilliseconds,
    EditorEventKind Kind,
    string? CommandId,
    IReadOnlyList<EntityId> EntityIds,
    string? Message);

public sealed record EditorDiagnostic(
    string Code,
    EditorDiagnosticSeverity Severity,
    string Message);

public sealed record EditorTool(
    string Id,
    string Label,
    string Capability);

public sealed record EditorSnapshot(
    string ProjectPath,
    EntityId ProjectId,
    string ProjectName,
    ProjectTargetProfile Target,
    ProjectBaseLevel BaseLevel,
    ProjectLevelSettings? LevelSettings,
    IReadOnlyList<EditorEntitySnapshot> Entities,
    IReadOnlyList<EntityId> Selection,
    bool IsDirty,
    bool MigrationPending,
    bool CanUndo,
    bool CanRedo,
    bool CanPaste,
    long LastEventSequence,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<EditorTool> Tools,
    IReadOnlyList<EditorDiagnostic> Diagnostics);
