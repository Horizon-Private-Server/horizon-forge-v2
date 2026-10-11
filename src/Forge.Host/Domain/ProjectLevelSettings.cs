namespace Forge.Host.Domain;

public sealed record ProjectLevelSceneSettings(
    float DeathHeight,
    bool IsSphericalWorld,
    ProjectVector3 SphereCenter,
    ProjectVector3 ShipPosition,
    float ShipRotationZ,
    ProjectIndexedReference ShipPath,
    ProjectIndexedReference ShipCameraCuboidStart,
    ProjectIndexedReference ShipCameraCuboidEnd);

public sealed record ProjectLevelSettings(
    ProjectRgb24 BackgroundColor,
    ProjectRgb24 FogColor,
    float FogNearDistance,
    float FogFarDistance,
    float FogNearIntensity,
    float FogFarIntensity,
    ProjectLevelSceneSettings? Scene = null);
