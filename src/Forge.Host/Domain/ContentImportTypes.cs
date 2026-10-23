namespace Forge.Host.Domain;

public static class ContentImportCapabilitySchema
{
    public const int CurrentVersion = 1;
}

public static class ContentImportPlanSchema
{
    public const int CurrentVersion = 3;
    public const int MaximumPlanItems = 100_000;
    public const int MaximumDiagnostics = 64;
}

public static class ContentImportBatchSchema
{
    public const int CurrentVersion = 1;
    public const int MaximumBatches = 1_024;
    public const int MaximumFamilies = 32;
    public const int MaximumStableIdsPerSet = ContentImportPlanSchema.MaximumPlanItems;
}

public static class ContentImportRemovalSchema
{
    public const int CurrentVersion = 1;
}

public enum ContentImportMode : byte
{
    Unsupported = 0,
    Add = 1,
    Replace = 2,
    AddAndReplace = 3,
}

public enum ContentImportSourceState : byte
{
    Unknown = 0,
    Absent = 1,
    Empty = 2,
    Present = 3,
    Malformed = 4,
}

public sealed record ContentImportFamilyInventory(
    string Family,
    ContentImportSourceState State,
    string? Diagnostic = null);

public sealed record ContentImportFamilyCapability(
    string Family,
    string Label,
    ContentImportMode Mode,
    ContentImportSourceState SourceState,
    bool ImplementationReady,
    bool Selectable,
    bool DefaultSelectedWhenReady,
    bool DefaultSelected,
    string? DisabledReason,
    string Ownership,
    IReadOnlyList<string> Dependencies,
    string ConflictNamespace,
    string NativeSemantics,
    string ProtectedSurvivorPolicy,
    string NoEditIdentity,
    IReadOnlyList<string> Limits,
    IReadOnlyList<string> Evidence);

public sealed record ContentImportCapabilityMatrix(
    int SchemaVersion,
    string Game,
    string Region,
    string Revision,
    IReadOnlyList<ContentImportFamilyCapability> Families);

public sealed record ContentImportSelection(string Family, ContentImportMode Mode);

public sealed record ContentImportFamilyPlan(
    string Family,
    string Label,
    ContentImportMode SupportedMode,
    ContentImportMode Mode,
    ContentImportSourceState SourceState,
    bool Selected,
    bool DefaultSelected,
    bool Selectable,
    IReadOnlyList<string> Add,
    IReadOnlyList<string> Reuse,
    IReadOnlyList<string> Remap,
    IReadOnlyList<string> Remove,
    IReadOnlyList<string> Protect,
    IReadOnlyList<string> DependencyReasons,
    IReadOnlyList<string> Conflicts,
    long EstimatedBytes,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Blockers);

public sealed record ContentImportReferenceRequirement(
    string Family,
    int OwnerSourceIndex,
    string FieldKey,
    int SourceValue,
    ProjectEntityKind TargetKind,
    bool Nullable,
    IReadOnlyList<EntityId> CandidateEntityIds);

public sealed record ContentImportPlan(
    int SchemaVersion,
    int CapabilitySchemaVersion,
    string BatchId,
    string Game,
    string Region,
    string Revision,
    string SourceFingerprint,
    string SourceLevelHash,
    int SourceLevel,
    string TargetProjectId,
    string TargetFingerprint,
    int TargetLevel,
    long EstimatedBytes,
    IReadOnlyList<ContentImportFamilyPlan> Families,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<ContentImportReferenceRequirement>? ReferenceRequirements = null);

public sealed record ContentImportBatchFamily(string Family, ContentImportMode Mode);

public sealed record ContentImportBatch(
    int SchemaVersion,
    string BatchId,
    int PlanSchemaVersion,
    int CapabilitySchemaVersion,
    long ImportedUnixMilliseconds,
    string SourceGame,
    string SourceRegion,
    string SourceRevision,
    string SourceFingerprint,
    string SourceLevelHash,
    int SourceLevel,
    IReadOnlyList<ContentImportBatchFamily> Families,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Reused,
    IReadOnlyList<string> Replaced,
    IReadOnlyList<string> Protected);

public sealed record ContentImportRemovalSurvivor(string StableId, string Reason);

public sealed record ContentImportRemovalPlan(
    int SchemaVersion,
    string BatchId,
    string TargetFingerprint,
    IReadOnlyList<string> Delete,
    IReadOnlyList<ContentImportRemovalSurvivor> Survivors,
    IReadOnlyList<string> Shared,
    IReadOnlyList<string> Protected,
    IReadOnlyList<string> Warnings);

internal enum ContentImportCommitPhase
{
    Prepared,
    BlobsStaged,
    Validated,
    Publishing,
}

internal sealed record ContentImportBatchCommit(
    string ExpectedTargetFingerprint,
    ContentImportBatch Batch,
    ForgeProjectContent Content,
    IReadOnlyList<ProjectAssetAttachment> Attachments,
    Action<ContentImportCommitPhase>? FaultInjector = null);

internal sealed record ContentImportRemovalCommit(
    string ExpectedTargetFingerprint,
    string BatchId,
    ForgeProjectContent Content);
