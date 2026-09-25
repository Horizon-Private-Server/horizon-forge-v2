namespace Forge.Host.Domain;

public sealed record ProjectLevelSettings(
    ProjectRgb24 BackgroundColor,
    ProjectRgb24 FogColor,
    float FogNearDistance,
    float FogFarDistance,
    float FogNearIntensity,
    float FogFarIntensity);
