namespace Forge.Host.Domain;

public enum EditorCommandKind : byte
{
    SetSelection = 1,
    RenameProject = 2,
    UpdateTransform = 3,
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
    string? Text = null);

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
    IReadOnlyList<ProjectEntity> Entities,
    IReadOnlyList<EntityId> Selection,
    bool IsDirty,
    bool MigrationPending,
    long LastEventSequence,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<EditorTool> Tools,
    IReadOnlyList<EditorDiagnostic> Diagnostics);
