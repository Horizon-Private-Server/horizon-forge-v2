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
    SetMobyInstanceProperty = 34,
    ImportMobyDexEntry = 35,
    RemoveMobyDexEntry = 36,
    InitializeMobyPVar = 37,
    SetMobyPVarField = 38,
    CreateGroup = 39,
    RenameGroup = 40,
    DeleteGroup = 41,
    ReorderGroup = 42,
    AddGroupMembers = 43,
    RemoveGroupMembers = 44,
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
    EditorFxEdit? FxEdit = null,
    EditorMobyPropertyEdit? MobyPropertyEdit = null,
    EditorMobyDexEdit? MobyDexEdit = null,
    EditorMobyPVarEdit? MobyPVarEdit = null,
    EditorGroupEdit? GroupEdit = null);

public sealed record EditorGroupEdit(
    GroupId? GroupId = null,
    int? DestinationOrder = null);

public sealed record EditorMobyDexEdit(
    byte[]? EntryJson = null,
    string? Game = null,
    int? OClass = null);

public enum EditorMobyPropertyValueKind : byte
{
    Integer = 1,
    Float = 2,
    Boolean = 3,
    Color = 4,
}

public sealed record EditorMobyColor(int R, int G, int B);

public sealed record EditorMobyPropertyValue(
    EditorMobyPropertyValueKind Kind,
    int? Integer = null,
    float? Float = null,
    bool? Boolean = null,
    EditorMobyColor? Color = null);

public sealed record EditorMobyPropertyDescriptor(
    string Key,
    string Label,
    EditorMobyPropertyValue Value,
    bool Editable,
    int? IntegerMinimum = null,
    int? IntegerMaximum = null,
    float? FloatMinimum = null,
    float? FloatMaximum = null,
    string? Unit = null,
    string? Help = null,
    string? ReadOnlyReason = null);

public sealed record EditorMobyPropertyEdit(
    string FieldKey,
    int ExpectedClassId,
    EditorMobyPropertyValue Value);

public delegate IReadOnlyList<EditorMobyPropertyDescriptor>? EditorMobyPropertyResolver(ProjectEntity entity);

public delegate Task EditorMobyPropertyCommandExecutor(
    ForgeProjectWorkspace workspace,
    EditorCommand command,
    CancellationToken cancellationToken);

public delegate Task EditorMobyPVarCommandExecutor(
    ForgeProjectWorkspace workspace,
    EditorCommand command,
    CancellationToken cancellationToken);

public delegate Task<EditorDiagnostic?> EditorProjectOpenHydrator(
    ForgeProjectWorkspace workspace,
    CancellationToken cancellationToken);

public delegate Task<EditorMapGroupCatalog> EditorMapGroupResolver(
    ForgeProjectWorkspace workspace,
    CancellationToken cancellationToken);

public enum EditorMobyPVarFieldKind : byte
{
    Group = 1,
    Integer = 2,
    Float = 3,
    Boolean = 4,
    Color = 5,
    Vector = 6,
    Choice = 7,
    Flags = 8,
    Reference = 9,
    Bytes = 10,
    Unknown = 11,
}

public enum EditorMobyPVarValueKind : byte
{
    Integer = 1,
    Float = 2,
    Boolean = 3,
    Color = 4,
    Vector = 5,
    Bytes = 6,
    Reference = 7,
}

public sealed record EditorMobyPVarValue(
    EditorMobyPVarValueKind Kind,
    string? Integer = null,
    double? Float = null,
    bool? Boolean = null,
    IReadOnlyList<byte>? Color = null,
    IReadOnlyList<double>? Vector = null,
    string? Bytes = null,
    EntityId? Reference = null);

public sealed record EditorMobyPVarOption(string Key, string Label, string Value);

public sealed record EditorMobyPVarReference(
    ProjectEntityKind TargetKind,
    EntityId? TargetEntityId,
    bool Nullable,
    int SourceValue,
    bool Missing);

public sealed record EditorMobyPVarFieldDescriptor(
    string Path,
    string Label,
    int Offset,
    int Length,
    EditorMobyPVarFieldKind Kind,
    EditorMobyPVarValue? Value,
    bool Editable,
    bool Invalid,
    string? Help = null,
    string? Minimum = null,
    string? Maximum = null,
    IReadOnlyList<EditorMobyPVarOption>? Options = null,
    EditorMobyPVarReference? Reference = null,
    IReadOnlyList<EditorMobyPVarFieldDescriptor>? Children = null);

public sealed record EditorMobyPVarDescriptor(
    string DatasetId,
    int DatasetVersion,
    int SchemaVersion,
    string SchemaSource,
    string SchemaFingerprint,
    string StateFingerprint,
    int Length,
    bool HasData,
    bool CanInitialize,
    string? Diagnostic,
    byte[]? RawData,
    byte[]? ModifiedByteMask,
    IReadOnlyList<EditorMobyPVarFieldDescriptor> Fields);

public sealed record EditorMobyPVarEdit(
    string FieldPath,
    int ExpectedClassId,
    string ExpectedDatasetId,
    int ExpectedDatasetVersion,
    int ExpectedSchemaVersion,
    string ExpectedSchemaFingerprint,
    string ExpectedStateFingerprint,
    EditorMobyPVarValue Value);

public delegate EditorMobyPVarDescriptor? EditorMobyPVarResolver(
    ForgeProjectWorkspace workspace,
    ProjectEntity entity,
    EditorMobyPVarResolutionIndex index,
    bool includeRawData);

public sealed record EditorMobyPVarResolutionIndex(
    IReadOnlyDictionary<EntityId, ProjectMobyPVar> PVars,
    IReadOnlyDictionary<(ProjectEntityKind Kind, int SourceIndex), EntityId?> EntitiesBySourceIndex);

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

public delegate string? EditorSourceClassNameResolver(ForgeProjectWorkspace workspace, ProjectEntity entity);

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
    bool? InstancedCollisionEnabled,
    IReadOnlyList<EditorMobyPropertyDescriptor>? MobyProperties,
    EditorMobyPVarDescriptor? MobyPVar,
    string? SourceClassName = null);

public sealed record EditorReferenceSnapshot(
    EntityId OwnerEntityId,
    ProjectReference Reference,
    int? SourceValue,
    bool Missing,
    string? DatasetSource = null);

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

public sealed record EditorGroupSnapshot(
    GroupId GroupId,
    string Name,
    IReadOnlyList<EntityId> Members,
    IReadOnlyList<EntityId> MissingMembers);

public enum EditorMapGroupKind : byte
{
    Moby = 1,
    Tie = 2,
    Shrub = 3,
}

public sealed record EditorMapGroupSource(
    EditorMapGroupKind Kind,
    int SourceIndex,
    string MemberSection,
    IReadOnlyList<int> MemberSourceIndices);

public sealed record EditorMapGroupCatalog(
    IReadOnlyList<EditorMapGroupSource> Groups,
    IReadOnlyList<EditorDiagnostic> Diagnostics);

public sealed record EditorMapGroupSnapshot(
    EditorMapGroupKind Kind,
    int SourceIndex,
    IReadOnlyList<EntityId> Members,
    IReadOnlyList<int> MissingSourceIndices);

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
    IReadOnlyList<EditorGroupSnapshot> Groups,
    IReadOnlyList<EditorMapGroupSnapshot> MapGroups,
    IReadOnlyList<EntityId> Selection,
    bool IsDirty,
    bool CanUndo,
    bool CanRedo,
    bool CanPaste,
    long LastEventSequence,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<EditorTool> Tools,
    IReadOnlyList<EditorDiagnostic> Diagnostics);
