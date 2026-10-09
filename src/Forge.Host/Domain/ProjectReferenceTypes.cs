namespace Forge.Host.Domain;

public enum ProjectReferenceDomain : byte
{
    Entity = 1,
    Asset = 2,
}

public enum ProjectEntityKind : byte
{
    Entity = 1,
    Moby = 2,
    Tie = 3,
    Shrub = 4,
    Tfrag = 5,
    Cuboid = 6,
    Sphere = 7,
    Cylinder = 8,
    Pill = 9,
    Spline = 10,
    GrindPath = 11,
    Area = 12,
    Collision = 13,
    SkyShell = 14,
    DirectionalLight = 15,
    PointLight = 16,
    EnvironmentSample = 17,
    EnvironmentTransition = 18,
    Camera = 19,
    AmbientSound = 20,
}

public sealed record ProjectReference(
    ProjectReferenceDomain Domain,
    string FieldKey,
    bool Nullable,
    ProjectEntityKind? EntityKind = null,
    EntityId? EntityId = null,
    AssetKind? AssetKind = null,
    AssetId? AssetId = null);

public sealed record ProjectReferenceEdge(
    EntityId OwnerEntityId,
    ProjectReference Reference,
    int? SourceValue = null);

public sealed record ProjectDeleteAnalysis(
    IReadOnlyList<EntityId> RequestedEntityIds,
    IReadOnlyList<EntityId> CascadedEntityIds,
    IReadOnlyList<ProjectReferenceEdge> ClearedReferences,
    IReadOnlyList<ProjectReferenceEdge> BlockingReferences);
