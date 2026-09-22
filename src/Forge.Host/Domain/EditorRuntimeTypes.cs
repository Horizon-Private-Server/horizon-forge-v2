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
    bool Invalid,
    bool MissingAsset);

public sealed record EditorEntitySnapshot(
    EntityId EntityId,
    string Name,
    string Layer,
    ProjectTransform Transform,
    ProjectAssetReference? Asset,
    ProjectEntityProvenance? Provenance,
    int? SourceClassId,
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
    long LastEventSequence,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<EditorTool> Tools,
    IReadOnlyList<EditorDiagnostic> Diagnostics);
