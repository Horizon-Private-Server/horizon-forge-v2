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
    IReadOnlyList<EditorTransformUpdate>? Transforms = null);

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

public sealed record EditorEntitySnapshot(
    EntityId EntityId,
    string Name,
    string Layer,
    ProjectTransform Transform,
    ProjectAssetReference? Asset,
    ProjectEntityProvenance? Provenance,
    int? SourceClassId,
    EditorEntityGeometry? Geometry,
    EditorEntityStatus State);

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
