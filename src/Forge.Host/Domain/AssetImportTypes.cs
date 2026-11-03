using RatchetPs2.Core.Games;

namespace Forge.Host.Domain;

public sealed record AssetImportRequest(
    GameId Game,
    string SourceIsoPath,
    string CatalogRootPath,
    string Fingerprint,
    string Revision,
    string ImporterVersion,
    bool Force = false);

public sealed record AssetImportResult(
    int CompletedLevels,
    int TotalLevels,
    int AssetAppearances,
    int UniqueAssets,
    int FailedAssets,
    bool Resumed);

public sealed record CatalogLevelAssets(IReadOnlyList<AssetCatalogPut> Assets, int FailedAssets);
