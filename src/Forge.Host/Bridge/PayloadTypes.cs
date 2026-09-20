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
    IReadOnlyList<string> Warnings);
