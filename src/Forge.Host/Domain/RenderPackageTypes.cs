namespace Forge.Host.Domain;

public sealed record RenderAssetResult(string AssetId, string Kind, string? Path, string? Error);

public sealed record RenderEnvironment(
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

public readonly record struct RenderOcclusionOctant(int X, int Y, int Z, int MaskIndex);

public sealed record RenderPackageResult(
    string RootPath,
    string CacheKey,
    IReadOnlyList<string> TerrainPaths,
    string? SkyPath,
    RenderEnvironment? Environment,
    IReadOnlyList<RenderAssetResult> Assets,
    bool CacheHit,
    IReadOnlyList<RenderOcclusionOctant>? OcclusionOctants = null);

public sealed record AssetPreviewRequest(
    string CacheRootPath,
    string CatalogRootPath,
    AssetId AssetId,
    AssetKind Kind,
    string TargetGame,
    string ViewPreset = "model-default",
    int? ShellIndex = null,
    string ProjectPath = "");

public sealed record AssetPreviewResult(string RootPath, string CacheKey, string ModelPath, bool CacheHit);
