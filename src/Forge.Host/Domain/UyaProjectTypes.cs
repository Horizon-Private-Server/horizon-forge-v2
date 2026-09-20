namespace Forge.Host.Domain;

public sealed record UyaProjectCreationOptions(
    IReadOnlyList<int> Levels,
    IReadOnlyList<string> Warnings);

public sealed record UyaProjectPreflight(
    int Level,
    int SourceInstanceCount,
    int RenderableInstanceCount,
    int ModelLessInstanceCount,
    int MissingAssetInstanceCount,
    int MissingClassCount,
    IReadOnlyList<string> Warnings);

public sealed record UyaProjectCreationRequest(
    string SourceIsoPath,
    string CatalogRootPath,
    string ProjectPath,
    string Name,
    string Fingerprint,
    string Revision,
    int Level,
    bool AllowPartial);

public sealed record ForgeProjectDescriptor(
    string Path,
    string Name,
    string TargetGame,
    string TargetRegion,
    string TargetRevision,
    string BakeProfile,
    int BaseLevel,
    long ModifiedUnixMilliseconds,
    int EntityCount,
    int MissingAssetCount,
    IReadOnlyList<string> Warnings);
