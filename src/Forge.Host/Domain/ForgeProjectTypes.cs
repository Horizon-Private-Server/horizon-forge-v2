namespace Forge.Host.Domain;

public sealed record ProjectTargetProfile(
    string Game,
    string Region,
    string Revision,
    string BakeProfile);

public sealed record ProjectBaseLevel(
    string Game,
    string Region,
    string Revision,
    int Level,
    string SourceFingerprint,
    int MissingAssetCount = 0,
    int EntityVersion = 0);

public sealed record ProjectVector3(float X, float Y, float Z);

public sealed record ProjectQuaternion(float X, float Y, float Z, float W);

public sealed record ProjectTransform(
    ProjectVector3 Position,
    ProjectQuaternion Rotation,
    ProjectVector3 Scale)
{
    public static ProjectTransform Identity { get; } = new(
        new(0, 0, 0),
        new(0, 0, 0, 1),
        new(1, 1, 1));
}

public sealed record ProjectAssetReference(AssetId Id, AssetKind Kind);

public sealed record ProjectEntityProvenance(
    string Game,
    int Level,
    string Section,
    int SourceIndex);

public sealed record ProjectEntityState(
    bool Hidden = false,
    bool Disabled = false,
    bool Locked = false);

public sealed record ProjectEntitySource(
    int ClassId,
    byte[] RawRecord,
    bool ModelLess = false);

public sealed record ProjectEntity(
    EntityId EntityId,
    string Name,
    string Layer,
    ProjectTransform Transform,
    ProjectAssetReference? Asset,
    ProjectEntityProvenance? Provenance = null,
    ProjectEntityState? State = null,
    ProjectEntitySource? Source = null,
    ProjectEntityGeometry? Geometry = null,
    ProjectEntityLighting? Lighting = null,
    ProjectTieLighting? TieLighting = null,
    ProjectCameraInstance? Camera = null,
    ProjectAmbientSoundInstance? AmbientSound = null);

public sealed record ProjectAttachedAsset(
    AssetId Id,
    AssetKind Kind,
    uint CanonicalFormatVersion,
    AssetId ParentId,
    long Size);

public sealed record ForgeProjectManifest(
    int SchemaVersion,
    string DocumentType,
    EntityId ProjectId,
    string Name,
    ProjectTargetProfile Target,
    ProjectBaseLevel BaseLevel,
    string Content);

public sealed record ForgeProjectContent(
    int SchemaVersion,
    string DocumentType,
    IReadOnlyList<ProjectEntity> Entities,
    IReadOnlyList<ProjectAttachedAsset> Assets,
    ProjectLevelSettings? LevelSettings = null);

public sealed record ProjectRecoverySnapshot(
    string Id,
    long CreatedUnixMilliseconds,
    string Name,
    int EntityCount,
    string Fingerprint,
    long Size);

public sealed record ProjectAssetReferenceChange(
    EntityId EntityId,
    ProjectAssetReference Previous,
    ProjectAssetReference Current);

public sealed record ProjectAssetEdit(
    AssetId DerivedAssetId,
    IReadOnlyList<ProjectAssetReferenceChange> Changes);
