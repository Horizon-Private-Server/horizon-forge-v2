namespace Forge.Host.Domain;

public sealed record ProjectCameraInstance(int PvarIndex, ProjectVector3 EulerRotation);

public sealed record ProjectAmbientSoundInstance(
    short MissionClass,
    uint UpdateFunctionPointer,
    int PvarIndex,
    float Range,
    IReadOnlyList<float> Matrix,
    IReadOnlyList<float> InverseRotationMatrix,
    ProjectVector3 EulerRotation,
    float Padding);
