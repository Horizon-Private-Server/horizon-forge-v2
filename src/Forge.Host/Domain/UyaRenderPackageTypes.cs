namespace Forge.Host.Domain;

public sealed record UyaRenderPackageRequest(
    string SourceIsoPath,
    string CacheRootPath,
    string Fingerprint,
    int Level);

public sealed record UyaRenderPackageResult(
    string RootPath,
    string CacheKey,
    IReadOnlyList<string> TerrainPaths,
    bool CacheHit);
