using Forge.Host.Domain;

namespace Forge.Host.Games.UYA;

internal sealed record UyaBaseLayerPayload(
    BakeLayerId Layer,
    string Name,
    AssetKind Kind,
    string SourceArchive,
    int SourceIndex,
    byte[] Bytes);

public sealed record UyaBaseLayerAsset(
    string Name,
    ProjectAssetReference Asset,
    uint CanonicalFormatVersion);

public sealed record UyaBaseLayerRecord(
    BakeLayerId Layer,
    string Encoding,
    IReadOnlyList<UyaBaseLayerAsset> Assets);

public sealed record UyaBaseLayerManifest(
    int SchemaVersion,
    string DocumentType,
    OpaqueContentSource Source,
    IReadOnlyList<UyaBaseLayerRecord> Layers);

public sealed record UyaBaseLayerInspection(
    UyaBaseLayerManifest? Manifest,
    IReadOnlyDictionary<BakeLayerId, IReadOnlyList<string>> Blockers)
{
    public bool IsValid => Manifest is not null && Blockers.Values.All(value => value.Count == 0);
}

public static class UyaBaseLayerSchema
{
    public const int CurrentVersion = 2;
    public const uint CanonicalFormatVersion = 0;
    public const string ManifestDocumentType = "horizon-forge-uya-base-layers";
    public const string Encoding = "uya-ntsc-u-native-v1";

    public static IReadOnlyList<BakeLayerId> Layers { get; } =
        [BakeLayerId.World, BakeLayerId.Sky, BakeLayerId.Tfrags, BakeLayerId.Collision, BakeLayerId.Lighting];
}
