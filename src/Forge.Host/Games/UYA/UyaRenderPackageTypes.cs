namespace Forge.Host.Games.UYA;

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
    float FogFarIntensity,
    float DeathHeight = 0,
    bool IsSphericalWorld = false,
    float SphereCenterX = 0,
    float SphereCenterY = 0,
    float SphereCenterZ = 0,
    float ShipPositionX = 0,
    float ShipPositionY = 0,
    float ShipPositionZ = 0,
    float ShipRotationZ = 0,
    int ShipPath = -1,
    int ShipCameraCuboidStart = -1,
    int ShipCameraCuboidEnd = -1,
    int ChunkPlaneCount = 0,
    int CoreSoundsCount = 0);

public readonly record struct UyaRenderOcclusionOctant(int X, int Y, int Z, int MaskIndex);

public sealed record UyaRenderPackageResult(
    string RootPath,
    string CacheKey,
    IReadOnlyList<string> TerrainPaths,
    string? SkyPath,
    UyaRenderEnvironmentResult? Environment,
    IReadOnlyList<UyaRenderAssetResult> Assets,
    bool CacheHit,
    IReadOnlyList<UyaRenderOcclusionOctant>? OcclusionOctants = null);
