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
    string Kind,
    string? Path,
    string? Error);

public sealed record UyaRenderEnvironmentResult(
    uint BackgroundRed,
    uint BackgroundGreen,
    uint BackgroundBlue,
    uint FogRed,
    uint FogGreen,
    uint FogBlue,
    float FogNearDistance,
    float FogFarDistance,
    float FogNearIntensity,
    float FogFarIntensity);

public sealed record UyaRenderPackageResult(
    string RootPath,
    string CacheKey,
    IReadOnlyList<string> TerrainPaths,
    string? SkyPath,
    UyaRenderEnvironmentResult? Environment,
    IReadOnlyList<UyaRenderAssetResult> Assets,
    bool CacheHit);
