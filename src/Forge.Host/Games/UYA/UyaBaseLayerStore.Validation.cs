using Forge.Host.Domain;
using System.Text.Json;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.Skyboxes;
using RatchetPs2.Core.Tfrags;
using RatchetPs2.Games.UYA.Gameplay;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.UYA;

public static partial class UyaBaseLayerStore
{
    private static void ValidateManifest(
        UyaBaseLayerManifest manifest,
        ForgeProjectWorkspace workspace,
        IReadOnlyDictionary<BakeLayerId, List<string>> blockers)
    {
        if (manifest.SchemaVersion != UyaBaseLayerSchema.CurrentVersion
            || manifest.DocumentType != UyaBaseLayerSchema.ManifestDocumentType
            || manifest.Layers is null
            || manifest.Source is null)
        {
            AddAll(blockers, "UYA base-layer manifest schema is invalid; re-import the base level.");
            return;
        }
        if (manifest.Source.Game != workspace.Manifest.BaseLevel.Game
            || manifest.Source.Region != workspace.Manifest.BaseLevel.Region
            || manifest.Source.Revision != workspace.Manifest.BaseLevel.Revision
            || manifest.Source.Level != workspace.Manifest.BaseLevel.Level
            || !manifest.Source.Fingerprint.Equals(workspace.Manifest.BaseLevel.SourceFingerprint, StringComparison.OrdinalIgnoreCase))
            AddAll(blockers, "UYA base-layer source does not match this project; re-import the base level.");
        foreach (var expected in UyaBaseLayerSchema.Layers)
        {
            var matches = manifest.Layers.Where(value => value?.Layer == expected).ToArray();
            if (matches.Length != 1 || matches[0]!.Encoding != UyaBaseLayerSchema.Encoding)
            {
                blockers[expected].Add($"{expected} declaration is missing or unsupported; re-import the base level.");
                continue;
            }
            var assets = matches[0]!.Assets;
            if (assets is null
                || assets.Any(value => value is null || !ValidAssetMetadata(expected, value))
                || assets.Select(value => value.Name).Distinct(StringComparer.Ordinal).Count() != assets.Count)
                blockers[expected].Add($"{expected} asset metadata is invalid; re-import the base level.");
        }
    }

    private static bool ValidAssetMetadata(BakeLayerId layer, UyaBaseLayerAsset asset) =>
        asset.CanonicalFormatVersion == UyaBaseLayerSchema.CanonicalFormatVersion
        && asset.Asset.Kind == ExpectedKind(layer)
        && BakeSchema.IsFingerprint(asset.Asset.Id.ToString())
        && !string.IsNullOrWhiteSpace(asset.Name)
        && Path.GetFileName(asset.Name) == asset.Name;

    private static int CollisionSourceIndex(string name)
    {
        if (name == "collision.bin") return 0;
        return ChunkSourceIndex(name, "Collision");
    }

    internal static int TfragSourceIndex(string name)
    {
        if (name == "primary.bin") return 0;
        return ChunkSourceIndex(name, "Tfrag");
    }

    private static int ChunkSourceIndex(string name, string label)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        if (!stem.StartsWith("chunk-", StringComparison.Ordinal)
            || !int.TryParse(stem.AsSpan("chunk-".Length), out var sourceIndex)
            || sourceIndex < 1)
            throw new InvalidDataException($"{label} source name {name} is invalid.");
        return sourceIndex;
    }

    private static AssetKind ExpectedKind(BakeLayerId layer) => layer switch
    {
        BakeLayerId.World => AssetKind.World,
        BakeLayerId.Sky => AssetKind.Sky,
        BakeLayerId.Tfrags => AssetKind.Tfrag,
        BakeLayerId.Collision => AssetKind.Collision,
        BakeLayerId.Lighting => AssetKind.Lighting,
        _ => throw new ArgumentOutOfRangeException(nameof(layer)),
    };

    private static bool IsVerifiedSource(AssetCatalogEntry entry, OpaqueContentSource source) =>
        entry.CanonicalFormatVersion == UyaBaseLayerSchema.CanonicalFormatVersion
        && entry.Tags.Contains("base-layer", StringComparer.Ordinal)
        && entry.Sources.Any(value => value.Game == source.Game
            && value.Region == source.Region
            && value.Revision == source.Revision
            && value.Level == $"level{source.Level:00}"
            && value.Fingerprint.Equals(source.Fingerprint, StringComparison.OrdinalIgnoreCase));

    private static void ValidatePayload(BakeLayerId layer, string name, byte[] bytes)
    {
        switch (layer)
        {
            case BakeLayerId.World when !UyaLevelSettingsReader.TryRead(bytes, out _):
                throw new InvalidDataException("UYA level settings are unreadable.");
            case BakeLayerId.Sky:
                using (var stream = new MemoryStream(bytes, writable: false)) SkyboxReader.Read(stream, GameId.UYA);
                break;
            case BakeLayerId.Tfrags:
                TfragTerrainReader.Read(bytes);
                break;
            case BakeLayerId.Collision:
                _ = CollisionConverter.Inspect(bytes, GameId.UYA);
                break;
            case BakeLayerId.Lighting when bytes.Length == 0:
                throw new InvalidDataException($"UYA lighting payload {name} is empty.");
        }
    }

    private static UyaBaseLayerInspection Missing(
        Dictionary<BakeLayerId, List<string>> blockers,
        string message)
    {
        AddAll(blockers, $"{message}; re-import it from the verified source ISO.");
        return new(null, blockers.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value));
    }

    private static void AddAll(IReadOnlyDictionary<BakeLayerId, List<string>> blockers, string message)
    {
        foreach (var values in blockers.Values) values.Add(message);
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await input.CopyToAsync(output, cancellationToken);
        await output.FlushAsync(cancellationToken);
        output.Flush(flushToDisk: true);
    }
}
