namespace Forge.Host.Domain;

public sealed record UyaRenderPackageRequest(
    string SourceIsoPath,
    string CacheRootPath,
    string Fingerprint,
    int Level,
    string ProjectPath,
    string CatalogRootPath);

public sealed record UyaRenderAssetResult(
    string AssetId,
    string? Path,
    string? Error);

public sealed record UyaRenderPackageResult(
    string RootPath,
    string CacheKey,
    IReadOnlyList<string> TerrainPaths,
    IReadOnlyList<UyaRenderAssetResult> Assets,
    bool CacheHit);
