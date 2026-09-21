namespace Forge.Host.Bridge;

public sealed record HostHandshake(
    string HostVersion,
    string SdkRevision,
    IReadOnlyList<string> SupportedGames,
    IReadOnlyList<string> Capabilities);

public readonly record struct EchoRequest(string Message, uint DelayMs);
public readonly record struct BridgeProgress(uint Completed, uint Total);
public sealed record UyaIsoValidationPayload(
    bool IsSupported,
    string Game,
    string Region,
    string Revision,
    string Serial,
    ulong Size,
    string Fingerprint,
    string Diagnostic);
public readonly record struct DevelopmentIsoRequest(string SourcePath, string TargetPath, string Fingerprint, bool Overwrite);
public sealed record DevelopmentIsoPayload(string Path, ulong Size, string Fingerprint);
public sealed record UyaAssetImportRequestPayload(
    string SourceIsoPath,
    string CatalogRootPath,
    string Fingerprint,
    string Revision,
    bool Force);
public readonly record struct UyaAssetImportResultPayload(
    uint CompletedLevels,
    uint TotalLevels,
    uint AssetAppearances,
    uint UniqueAssets,
    uint FailedAssets,
    bool Resumed);
public sealed record UyaProjectOptionsPayload(
    IReadOnlyList<uint> Levels,
    IReadOnlyList<string> Warnings);
public sealed record UyaProjectCreationRequestPayload(
    string SourceIsoPath,
    string CatalogRootPath,
    string ProjectPath,
    string Name,
    string Fingerprint,
    string Revision,
    uint Level,
    bool AllowPartial);
public readonly record struct UyaProjectPreflightRequestPayload(string SourceIsoPath, string CatalogRootPath, uint Level);
public sealed record UyaProjectPreflightPayload(
    uint Level,
    uint SourceInstanceCount,
    uint RenderableInstanceCount,
    uint ModelLessInstanceCount,
    uint MissingAssetInstanceCount,
    uint MissingClassCount,
    IReadOnlyList<string> Warnings);
public readonly record struct ProjectInspectRequestPayload(string ProjectPath, string CatalogRootPath);
public readonly record struct ProjectRenameRequestPayload(string ProjectPath, string CatalogRootPath, string Name);
public readonly record struct ProjectRecoveryRequestPayload(string ProjectPath, string CatalogRootPath, string RecoveryId);
public readonly record struct ProjectAssetRepairRequestPayload(string ProjectPath, string CatalogRootPath, string SourceIsoPath);
public sealed record CatalogMaintenanceRequestPayload(string CatalogRootPath, IReadOnlyList<string> ProjectRoots);
public sealed record CatalogCollectionRequestPayload(
    string CatalogRootPath,
    IReadOnlyList<string> ProjectRoots,
    string ConfirmationToken);
public sealed record UyaRenderPackageRequestPayload(
    string SourceIsoPath,
    string CacheRootPath,
    string Fingerprint,
    uint Level,
    string ProjectPath,
    string CatalogRootPath);
public sealed record UyaRenderAssetPayload(string AssetId, string? Path, string? Error);
public sealed record UyaRenderPackageResultPayload(
    string RootPath,
    string CacheKey,
    IReadOnlyList<string> TerrainPaths,
    IReadOnlyList<UyaRenderAssetPayload> Assets,
    bool CacheHit);
public sealed record CatalogMaintenancePayload(
    uint ProjectCount,
    uint CatalogAssetCount,
    uint ProtectedAssetCount,
    uint CandidateCount,
    uint CatalogCandidateCount,
    ulong CandidateBytes,
    string ConfirmationToken,
    IReadOnlyList<string> CandidateKinds,
    IReadOnlyList<string> Blockers);
public sealed record ProjectRecoverySnapshotPayload(
    string Id,
    ulong CreatedUnixMilliseconds,
    string Name,
    uint EntityCount,
    string Fingerprint,
    ulong Size);
public sealed record MissingProjectAssetPayload(
    string Id,
    string Kind,
    uint EntityCount,
    bool Repairable,
    IReadOnlyList<string> Provenance);
public sealed record ForgeProjectDescriptorPayload(
    string Path,
    string Name,
    string TargetGame,
    string TargetRegion,
    string TargetRevision,
    string BakeProfile,
    uint BaseLevel,
    ulong ModifiedUnixMilliseconds,
    uint EntityCount,
    uint MissingAssetCount,
    bool IsDirty,
    bool MigrationPending,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<ProjectRecoverySnapshotPayload> Recoveries,
    IReadOnlyList<MissingProjectAssetPayload> MissingAssets);
