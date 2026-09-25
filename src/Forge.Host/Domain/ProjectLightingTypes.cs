namespace Forge.Host.Domain;

public sealed record ProjectRgb24(byte R, byte G, byte B);
public sealed record ProjectRgba32(byte R, byte G, byte B, byte A);

public sealed record ProjectDirectionalLight(
    ProjectVector4 TopColor,
    ProjectVector4 TopDirection,
    ProjectVector4 InverseColor,
    ProjectVector4 InverseDirection);

public sealed record ProjectPointLight(
    ushort PositionX,
    ushort PositionY,
    ushort PositionZ,
    ushort Radius,
    ushort ColorR,
    ushort ColorG,
    ushort ColorB,
    ushort UnknownE);

public sealed record ProjectEnvironmentSamplePoint(
    int HeroLight,
    short PositionX,
    short PositionY,
    short PositionZ,
    short ReverbDepth,
    short MusicTrack,
    byte FogNearIntensity,
    byte FogFarIntensity,
    ProjectRgb24 HeroColor,
    byte ReverbType,
    byte ReverbDelay,
    byte ReverbFeedback,
    byte EnableReverbParameters,
    ProjectRgb24 FogColor,
    short FogNearDistance,
    short FogFarDistance,
    ushort Unknown1E);

public sealed record ProjectEnvironmentTransition(
    ProjectVector4 BoundingSphere,
    IReadOnlyList<float> InverseMatrix,
    ProjectRgba32 HeroColor1,
    ProjectRgba32 HeroColor2,
    int HeroLight1,
    int HeroLight2,
    uint Flags,
    ProjectRgba32 FogColor1,
    ProjectRgba32 FogColor2,
    float FogNearDistance1,
    float FogNearIntensity1,
    float FogFarDistance1,
    float FogFarIntensity1,
    float FogNearDistance2,
    float FogNearIntensity2,
    float FogFarDistance2,
    float FogFarIntensity2,
    int Unknown7C);

public sealed record ProjectEntityLighting(
    ProjectDirectionalLight? DirectionalLight = null,
    ProjectPointLight? PointLight = null,
    ProjectEnvironmentSamplePoint? EnvironmentSamplePoint = null,
    ProjectEnvironmentTransition? EnvironmentTransition = null);

public sealed record ProjectTieLighting(int DirectionalLights, byte[] AmbientRgbas);
