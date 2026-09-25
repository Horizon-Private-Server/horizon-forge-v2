using Forge.Host.Domain;

namespace Forge.Host.Games.UYA;

public sealed record UyaGameplaySourceSection(
    string Name,
    int Size,
    string Sha256,
    string Blob);

public sealed record UyaGameplaySourceManifest(
    int SchemaVersion,
    string DocumentType,
    OpaqueContentSource Source,
    IReadOnlyList<UyaGameplaySourceSection> Sections);

public sealed record UyaGameplayInspection(
    UyaGameplaySourceManifest? Manifest,
    IReadOnlyList<string> Blockers)
{
    public bool IsValid => Manifest is not null && Blockers.Count == 0;
}

public sealed record UyaGameplayBakeSection(
    string Name,
    string Path,
    int Size,
    string Sha256);

public sealed record UyaGameplayMobyReference(
    int TargetIndex,
    EntityId EntityId,
    int Uid,
    int PvarIndex);

public sealed record UyaGameplayBakeManifest(
    int SchemaVersion,
    string DocumentType,
    string Encoding,
    IReadOnlyList<UyaGameplayBakeSection> Sections,
    IReadOnlyList<UyaGameplayMobyReference> Mobys,
    IReadOnlyList<UyaGameplayInstanceReference> Instances);

public sealed record UyaGameplayInstanceReference(
    string Section,
    int SourceIndex,
    EntityId EntityId,
    ProjectTransform Transform);

public static class UyaGameplayLayerSchema
{
    public const int CurrentVersion = 3;
    public const string SourceDocumentType = "horizon-forge-uya-gameplay-source";
    public const string BakeDocumentType = "horizon-forge-uya-gameplay-bake";
    public const string Encoding = "uya-ntsc-u-native-v1";
    public const string RelativeRootPath = "content/uya-gameplay";
    public const string ManifestFileName = "manifest.json";

    public static IReadOnlyList<string> SectionNames { get; } =
    [
        "pvar_moby_links", "pvar_table", "pvar_data", "pvar_relative_pointers",
        "cameras", "sound_instances", "cuboids", "spheres", "cylinders", "pills", "splines",
    ];

    public static IReadOnlyList<string> WritableInstanceSections { get; } =
    [
        "cameras", "sound_instances", "cuboids", "spheres", "cylinders", "pills", "splines",
    ];
}
