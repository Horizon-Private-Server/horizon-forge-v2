namespace Forge.Host.Games.UYA;

public sealed record UyaRenderPackageRequest(
    string SourceIsoPath,
    string CacheRootPath,
    string Fingerprint,
    int Level,
    string ProjectPath,
    string CatalogRootPath);
