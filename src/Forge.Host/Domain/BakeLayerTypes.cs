namespace Forge.Host.Domain;

public enum BakeLayerId
{
    World,
    Sky,
    Tfrags,
    Collision,
    Ties,
    Shrubs,
    Mobys,
    Gameplay,
    Lighting,
    Opaque,
}

public enum BakeLayerState
{
    Dirty,
    DependencyInvalidated,
    Clean,
    Blocked,
}

public sealed record BakeLayerDefinition(
    BakeLayerId Id,
    IReadOnlyList<BakeLayerId> Dependencies);

public sealed record BakeLayerInput(
    BakeLayerId Id,
    ReadOnlyMemory<byte> AuthoritativeContent,
    IReadOnlyList<AssetId> AssetIds,
    ReadOnlyMemory<byte> RelevantSettings,
    IReadOnlyList<string>? Blockers = null);

public sealed record BakeFingerprintContext(
    ProjectTargetProfile Target,
    string TranslatorVersion,
    string BakerVersion);

public sealed record BakeLayerSnapshot(
    BakeLayerId Layer,
    string ContentFingerprint,
    string InputFingerprint,
    string OutputFingerprint,
    string RelativePath,
    long Size);

public sealed record BakeManifest(
    int SchemaVersion,
    string DocumentType,
    IReadOnlyList<BakeLayerSnapshot> Layers,
    PaletteBakeReport? PaletteReport = null);

public sealed record BakeLayerPlan(
    BakeLayerId Layer,
    BakeLayerState State,
    string ContentFingerprint,
    string InputFingerprint,
    IReadOnlyList<BakeLayerId> Dependencies,
    IReadOnlyList<string> Blockers);

public sealed record BakePlan(IReadOnlyList<BakeLayerPlan> Layers);

public static class BakeSchema
{
    public const int CurrentVersion = 1;
    public const string ManifestDocumentType = "horizon-forge-bake-manifest";

    public static bool IsFingerprint(string value) =>
        value.Length == AssetId.TextLength
        && value.All(character => Uri.IsHexDigit(character) && !char.IsUpper(character));

    public static bool UsesSharedPalette(BakeLayerId layer) =>
        layer is BakeLayerId.Ties or BakeLayerId.Shrubs or BakeLayerId.Mobys;
}
