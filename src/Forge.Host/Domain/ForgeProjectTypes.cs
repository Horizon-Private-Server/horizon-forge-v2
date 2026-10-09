using System.Text.Json.Serialization;

namespace Forge.Host.Domain;

public sealed record ProjectTargetProfile(
    string Game,
    string Region,
    string Revision,
    string BakeProfile)
{
    public ProjectPaletteOptimization PaletteOptimization { get; init; } = ProjectPaletteOptimization.Default;
}

public sealed record ProjectPaletteOptimization(string MappingVersion, int Strength)
{
    public const string CurrentMappingVersion = "paletteOptimization.v1";
    public const int DefaultStrength = 50;

    public static ProjectPaletteOptimization Default { get; } =
        new(CurrentMappingVersion, DefaultStrength);
}

public sealed record ProjectBaseLevel(
    string Game,
    string Region,
    string Revision,
    int Level,
    string SourceFingerprint,
    int MissingAssetCount = 0);

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
    bool ModelLess = false,
    int? SourceIndex = null);

public sealed record ProjectSkyShell(
    int SourceShellIndex,
    int Order,
    ProjectVector3 InitialRotationRadians,
    ProjectVector3 AngularVelocityRadiansPerSecond);

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
    ProjectAmbientSoundInstance? AmbientSound = null,
    ProjectSkyShell? SkyShell = null,
    ProjectCollisionPiece? Collision = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? InstancedCollisionEnabled = null);

public sealed record ProjectAttachedAsset(
    AssetId Id,
    AssetKind Kind,
    uint CanonicalFormatVersion,
    AssetId? ParentId,
    long Size);

public static class ProjectTextureAssetSchema
{
    public const uint CanonicalFormatVersion = 1;
    public const long MaximumCanonicalBytes = 64L * 1024 * 1024;
}

public static class ProjectAssetOverrideSchema
{
    public const int CurrentVersion = 1;
}

public sealed record ProjectAssetOverride(
    int SchemaVersion,
    ProjectAssetReference Source,
    ProjectAssetReference Replacement);

public static class ProjectHudSchema
{
    public const int CurrentVersion = 1;
    public const int PhysicalBankCount = 5;
    public const int MaximumIconCount = 1_024;
}

public sealed record ProjectHudSourceFrame(
    int SourceFrameIndex,
    int SourcePaletteIndex,
    int SourceTextureIndex,
    int PaletteBankIndex,
    int TextureBankIndex,
    int Width,
    int Height,
    ProjectAssetReference? Texture,
    string? Diagnostic = null);

public sealed record ProjectHudSourceIcon(
    int SourceIconIndex,
    ushort SpriteId,
    IReadOnlyList<ProjectHudSourceFrame> Frames);

public sealed record ProjectHudIconAddition(
    ushort SpriteId,
    int BankIndex,
    int Width,
    int Height,
    ProjectAssetReference Texture);

public sealed record ProjectHudState(
    int SchemaVersion,
    int MinimumAppendBank,
    IReadOnlyList<ProjectHudSourceIcon> SourceIcons,
    IReadOnlyList<ProjectHudIconAddition> Additions);

public static class ProjectFxSchema
{
    public const int CurrentVersion = 1;
    public const int MaximumTextureCount = 4_096;
}

public sealed record ProjectFxSourceTexture(
    int SourceIndex,
    string Label,
    int Width,
    int Height,
    int PaletteOffset,
    int PixelOffset,
    bool IsSwizzled,
    ProjectAssetReference? Texture,
    string? Diagnostic = null);

public sealed record ProjectFxTextureAddition(
    int Width,
    int Height,
    ProjectAssetReference Texture);

public sealed record ProjectFxState(
    int SchemaVersion,
    IReadOnlyList<ProjectFxSourceTexture> SourceTextures,
    IReadOnlyList<ProjectFxTextureAddition> Additions);

public sealed record ProjectResolvedAsset(
    ProjectAssetReference Source,
    ProjectAssetReference Effective,
    uint CanonicalFormatVersion,
    long Size,
    string Path,
    bool ProjectAttached);

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
    IReadOnlyList<ProjectInstancedCollisionBinding> InstancedCollisionBindings,
    IReadOnlyList<ProjectAssetOverride> AssetOverrides,
    ProjectLevelSettings? LevelSettings = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ProjectHudState? Hud = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ProjectFxState? Fx = null);

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
