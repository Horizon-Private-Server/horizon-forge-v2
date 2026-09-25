namespace Forge.Host.Domain;

public sealed record ProjectVector4(float X, float Y, float Z, float W);

public sealed record ProjectShapeGeometry(
    IReadOnlyList<float> Matrix,
    IReadOnlyList<float> InverseRotationMatrix,
    ProjectVector3 EulerRotation,
    ProjectCameraCollision? CameraCollision = null);

public sealed record ProjectCameraCollision(
    int Flags,
    int IntValue,
    float FloatValue,
    ProjectVector4 BoundingSphere);

public sealed record ProjectSplineGeometry(IReadOnlyList<ProjectVector4> Points);

public sealed record ProjectGrindPathGeometry(
    ProjectVector4 BoundingSphere,
    int Unknown4,
    int Wrap,
    int Inactive,
    IReadOnlyList<ProjectVector4> Points);

public sealed record ProjectGeometryLink(int SourceIndex, EntityId? EntityId);

public sealed record ProjectAreaGeometry(
    ProjectVector4 BoundingSphere,
    short LastUpdateTime,
    IReadOnlyList<ProjectGeometryLink> Splines,
    IReadOnlyList<ProjectGeometryLink> Cuboids,
    IReadOnlyList<ProjectGeometryLink> Spheres,
    IReadOnlyList<ProjectGeometryLink> Cylinders,
    IReadOnlyList<ProjectGeometryLink> NegativeCuboids);

public sealed record ProjectEntityGeometry(
    ProjectShapeGeometry? Cuboid = null,
    ProjectShapeGeometry? Sphere = null,
    ProjectShapeGeometry? Cylinder = null,
    ProjectShapeGeometry? Pill = null,
    ProjectSplineGeometry? Spline = null,
    ProjectGrindPathGeometry? GrindPath = null,
    ProjectAreaGeometry? Area = null);
