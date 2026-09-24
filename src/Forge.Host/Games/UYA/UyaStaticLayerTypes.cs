using Forge.Host.Domain;

namespace Forge.Host.Games.UYA;

public sealed record UyaStaticLayerSourceRecord(
    BakeLayerId Layer,
    int RecordSize,
    IReadOnlyList<int> HeaderWords,
    byte[] TrailingBytes);

public sealed record UyaStaticLayerSourceManifest(
    int SchemaVersion,
    string DocumentType,
    OpaqueContentSource Source,
    IReadOnlyList<UyaStaticLayerSourceRecord> Layers);

public sealed record UyaStaticLayerInspection(
    UyaStaticLayerSourceManifest? Manifest,
    IReadOnlyDictionary<BakeLayerId, IReadOnlyList<string>> Blockers)
{
    public bool IsValid => Manifest is not null && Blockers.Values.All(value => value.Count == 0);
}

public sealed record UyaStaticBakeDefinition(
    int TargetIndex,
    int ClassId,
    ProjectAssetReference Asset,
    uint CanonicalFormatVersion,
    string Resource);

public sealed record UyaStaticBakeInstance(
    int TargetIndex,
    EntityId EntityId,
    int DefinitionIndex,
    int ClassId);

public sealed record UyaStaticBakeManifest(
    int SchemaVersion,
    string DocumentType,
    BakeLayerId Layer,
    string Encoding,
    int InstancesSize,
    string InstancesSha256,
    IReadOnlyList<UyaStaticBakeDefinition> Definitions,
    IReadOnlyList<UyaStaticBakeInstance> Instances);

public static class UyaStaticLayerSchema
{
    public const int CurrentVersion = 2;
    public const uint CanonicalFormatVersion = UyaAssetImportService.CanonicalFormatVersion;
    public const string SourceDocumentType = "horizon-forge-uya-static-source";
    public const string BakeDocumentType = "horizon-forge-uya-static-bake";
    public const string Encoding = "uya-ntsc-u-native-v1";
    public const string RelativeSourcePath = "content/uya-static-source.json";

    public static IReadOnlyList<BakeLayerId> Layers { get; } =
        [BakeLayerId.Ties, BakeLayerId.Shrubs, BakeLayerId.Mobys];
}
