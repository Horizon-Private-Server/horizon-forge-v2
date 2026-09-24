namespace Forge.Host.Games.UYA;

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
