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
    RemoveInstancedCollisionProxy = 20,
    SetInstancedCollisionEnabled = 21,
    SetInstancedCollisionRawType = 22,
    SetInstancedCollisionFaceTypes = 23,
    SetEntityReference = 24,
    UpdatePaletteOptimization = 25,
    ReplaceHudTexture = 26,
    RemoveHudTextureOverride = 27,
    AddHudIcon = 28,
    RemoveHudIcon = 29,
    ReplaceFxTexture = 30,
    RemoveFxTextureOverride = 31,
    AddFxTexture = 32,
    RemoveFxTexture = 33,
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
    bool? InstancedCollisionEnabled = null,
    byte? InstancedCollisionRawType = null,
    AssetId? InstancedCollisionProxyAssetId = null,
    IReadOnlyList<ProjectCollisionFaceTypeOverride>? InstancedCollisionFaceTypes = null,
    EditorReferenceUpdate? ReferenceUpdate = null,
    ProjectPaletteOptimization? PaletteOptimization = null,
    EditorHudEdit? HudEdit = null,
    EditorFxEdit? FxEdit = null);

public sealed record EditorHudEdit(
    AssetId? SourceAssetId = null,
    ushort? SpriteId = null,
    int? BankIndex = null,
    string? ImageFormat = null,
    byte[]? ImageBytes = null);

public delegate Task EditorHudCommandExecutor(
    ForgeProjectWorkspace workspace,
    EditorCommand command,
    CancellationToken cancellationToken);

public sealed record EditorFxEdit(
    AssetId? SourceAssetId = null,
    int? Index = null,
    string? ImageFormat = null,
    byte[]? ImageBytes = null);

public delegate Task EditorFxCommandExecutor(
    ForgeProjectWorkspace workspace,
    EditorCommand command,
    CancellationToken cancellationToken);

public sealed record EditorReferenceUpdate(
    string FieldKey,
    int? SourceValue,
    EntityId? TargetEntityId);

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
    EditorInstancedCollisionBindingSnapshot? InstancedCollision,
    EditorInstancedCollisionBindingSnapshot? IndividualInstancedCollision,
    bool? InstancedCollisionEnabled);

public sealed record EditorReferenceSnapshot(
    EntityId OwnerEntityId,
    ProjectReference Reference,
    int? SourceValue,
    bool Missing);

public sealed record EditorHudFrameSnapshot(
    int SourceFrameIndex,
    int SourcePaletteIndex,
    int SourceTextureIndex,
    int PaletteBankIndex,
    int TextureBankIndex,
    int Width,
    int Height,
    ProjectAssetReference? SourceTexture,
    ProjectAssetReference? EffectiveTexture,
    string? Diagnostic);

public sealed record EditorHudIconSnapshot(
    int SourceIconIndex,
    ushort SpriteId,
    IReadOnlyList<EditorHudFrameSnapshot> Frames);

public sealed record EditorHudSnapshot(
    bool CanRead,
    bool IsDirty,
    bool CanReplace,
    bool CanAppend,
    string? AuthoringDisabledReason,
    int PhysicalBankCount,
    int MinimumAppendBank,
    ushort MinimumAppendSpriteId,
    ushort MaximumAppendSpriteId,
    int MaximumIconCount,
    IReadOnlyList<EditorHudIconSnapshot> SourceIcons,
    IReadOnlyList<ProjectHudIconAddition> Additions);

public sealed record EditorFxSourceTextureSnapshot(
    int SourceIndex,
    string Label,
    int Width,
    int Height,
    int PaletteOffset,
    int PixelOffset,
    bool IsSwizzled,
    ProjectAssetReference? SourceTexture,
    ProjectAssetReference? EffectiveTexture,
    string? Diagnostic);

public sealed record EditorFxSnapshot(
    bool CanRead,
    bool IsDirty,
    bool CanReplace,
    bool CanAppend,
    string? AuthoringDisabledReason,
    int MaximumTextureCount,
    IReadOnlyList<EditorFxSourceTextureSnapshot> SourceTextures,
    IReadOnlyList<ProjectFxTextureAddition> Additions);

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
    EditorHudSnapshot? Hud,
    EditorFxSnapshot? Fx,
    IReadOnlyList<EditorEntitySnapshot> Entities,
    IReadOnlyList<EditorReferenceSnapshot> References,
    IReadOnlyList<EntityId> Selection,
    bool IsDirty,
    bool CanUndo,
    bool CanRedo,
    bool CanPaste,
    long LastEventSequence,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<EditorTool> Tools,
    IReadOnlyList<EditorDiagnostic> Diagnostics);
