namespace Forge.Host.Games.UYA;

public sealed record UyaAssetImportRequest(
    string SourceIsoPath,
    string CatalogRootPath,
    string Fingerprint,
    string Revision,
    string ImporterVersion,
    bool Force = false);

public sealed record UyaAssetImportResult(
    int CompletedLevels,
    int TotalLevels,
    int AssetAppearances,
    int UniqueAssets,
    int FailedAssets,
    bool Resumed);
