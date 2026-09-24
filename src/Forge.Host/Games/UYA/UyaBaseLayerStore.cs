using Forge.Host.Domain;
using System.Text.Json;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.Skyboxes;
using RatchetPs2.Core.Tfrags;
using RatchetPs2.Games.UYA.Gameplay;

namespace Forge.Host.Games.UYA;

public static class UyaBaseLayerStore
{
    public const string RelativeManifestPath = "content/uya-base-layers.json";

    internal static async Task WriteAsync(
        string projectRoot,
        OpaqueContentSource source,
        IReadOnlyList<UyaBaseLayerPayload> payloads,
        IReadOnlyList<AssetCatalogEntry> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(payloads);
        ArgumentNullException.ThrowIfNull(entries);
        if (payloads.Count != entries.Count) throw new ArgumentException("Base layer payload and catalog counts differ.");
        var assets = payloads.Zip(entries).Select(pair => new
        {
            pair.First.Layer,
            Asset = new UyaBaseLayerAsset(
                pair.First.Name,
                new(pair.Second.Id, pair.Second.Kind),
                pair.Second.CanonicalFormatVersion),
        }).ToArray();
        var layers = UyaBaseLayerSchema.Layers.Select(layer => new UyaBaseLayerRecord(
            layer,
            UyaBaseLayerSchema.Encoding,
            assets.Where(value => value.Layer == layer).Select(value => value.Asset)
                .OrderBy(value => value.Name, StringComparer.Ordinal).ToArray())).ToArray();
        var manifest = new UyaBaseLayerManifest(
            UyaBaseLayerSchema.CurrentVersion,
            UyaBaseLayerSchema.ManifestDocumentType,
            source with { Fingerprint = source.Fingerprint.ToLowerInvariant() },
            layers);
        await ForgeProjectPersistence.WriteFileSafelyAsync(
            ForgeProjectPersistence.ResolveRelativePath(projectRoot, RelativeManifestPath),
            ForgeProjectPersistence.Serialize(manifest),
            cancellationToken);
    }

    public static async Task<UyaBaseLayerInspection> InspectAsync(
        string projectRoot,
        AssetCatalogStore catalog,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var blockers = UyaBaseLayerSchema.Layers.ToDictionary(layer => layer, _ => new List<string>());
        var path = ForgeProjectPersistence.ResolveRelativePath(projectRoot, RelativeManifestPath);
        if (!File.Exists(path)) return Missing(blockers, "UYA base-layer manifest is missing");
        UyaBaseLayerManifest manifest;
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Base-layer manifest cannot be a symbolic link.");
            manifest = ForgeProjectPersistence.Deserialize<UyaBaseLayerManifest>(
                await File.ReadAllBytesAsync(path, cancellationToken), "UYA base-layer manifest");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return Missing(blockers, $"UYA base-layer manifest is invalid ({exception.Message})");
        }

        var workspace = await ForgeProjectWorkspace.OpenAsync(projectRoot, cancellationToken);
        ValidateManifest(manifest, workspace, blockers);
        foreach (var layer in manifest.Layers ?? [])
        {
            if (layer is null || !blockers.ContainsKey(layer.Layer)) continue;
            foreach (var asset in layer.Assets ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (asset is null || !ValidAssetMetadata(layer.Layer, asset)) continue;
                var catalogEntry = catalog.Query(new(Id: asset.Asset.Id)).SingleOrDefault();
                if (catalogEntry is null || !IsVerifiedSource(catalogEntry, manifest.Source))
                {
                    blockers[layer.Layer].Add(
                        $"{layer.Layer} source {asset.Name} is not a verified target-native UYA asset; restore it from the source ISO.");
                    continue;
                }
                var assetPath = workspace.ResolveAssetPath(asset.Asset.Id, catalog);
                if (assetPath is null)
                {
                    blockers[layer.Layer].Add(
                        $"{layer.Layer} source {asset.Name} is missing; re-import level {manifest.Source.Level} from the verified source ISO.");
                    continue;
                }
                try
                {
                    var bytes = await File.ReadAllBytesAsync(assetPath, cancellationToken);
                    ValidatePayload(layer.Layer, asset.Name, bytes);
                }
                catch (Exception exception) when (exception is InvalidDataException or IOException or OverflowException)
                {
                    blockers[layer.Layer].Add(
                        $"{layer.Layer} source {asset.Name} is unsupported or invalid ({exception.Message}).");
                }
            }
        }
        return new(manifest, blockers.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value));
    }

    internal static async Task<IReadOnlyList<AssetId>> ReadAssetIdsAsync(
        string projectRoot,
        CancellationToken cancellationToken = default)
    {
        var path = ForgeProjectPersistence.ResolveRelativePath(projectRoot, RelativeManifestPath);
        if (!File.Exists(path)) return [];
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Base-layer manifest cannot be a symbolic link.");
        var manifest = ForgeProjectPersistence.Deserialize<UyaBaseLayerManifest>(
            await File.ReadAllBytesAsync(path, cancellationToken), "UYA base-layer manifest");
        if (manifest.SchemaVersion != UyaBaseLayerSchema.CurrentVersion
            || manifest.DocumentType != UyaBaseLayerSchema.ManifestDocumentType
            || manifest.Layers is null
            || manifest.Layers.Any(layer => layer?.Assets is null
                || layer.Assets.Any(asset => asset is null || !BakeSchema.IsFingerprint(asset.Asset.Id.ToString()))))
            throw new InvalidDataException("UYA base-layer manifest is invalid.");
        return manifest.Layers.SelectMany(layer => layer.Assets).Select(asset => asset.Asset.Id).Distinct().ToArray();
    }

    public static async Task<IReadOnlyList<BakeLayerInput>> CreateBakeInputsAsync(
        string projectRoot,
        AssetCatalogStore catalog,
        CancellationToken cancellationToken = default)
    {
        var inspection = await InspectAsync(projectRoot, catalog, cancellationToken);
        return UyaBaseLayerSchema.Layers.Select(layer =>
        {
            var record = inspection.Manifest?.Layers?.SingleOrDefault(value => value.Layer == layer);
            var content = record is null || inspection.Manifest is null
                ? ReadOnlyMemory<byte>.Empty
                : ForgeProjectPersistence.Serialize(new UyaBaseLayerManifest(
                    inspection.Manifest.SchemaVersion,
                    inspection.Manifest.DocumentType,
                    inspection.Manifest.Source,
                    [record]));
            return new BakeLayerInput(
                layer,
                content,
                record?.Assets?.Select(value => value.Asset.Id).ToArray() ?? [],
                ReadOnlyMemory<byte>.Empty,
                inspection.Blockers[layer]);
        }).ToArray();
    }

    public static async Task<BakeLayerSnapshot> StageAsync(
        string projectRoot,
        AssetCatalogStore catalog,
        BakeStagingStore staging,
        BakeLayerPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(staging);
        ArgumentNullException.ThrowIfNull(plan);
        if (!UyaBaseLayerSchema.Layers.Contains(plan.Layer))
            throw new ArgumentException("Plan is not a UYA base layer.", nameof(plan));
        var inspection = await InspectAsync(projectRoot, catalog, cancellationToken);
        var blockers = inspection.Blockers[plan.Layer];
        if (blockers.Count > 0) throw new InvalidDataException(string.Join(' ', blockers));
        var record = inspection.Manifest!.Layers.Single(value => value.Layer == plan.Layer);
        var workspace = await ForgeProjectWorkspace.OpenAsync(projectRoot, cancellationToken);
        var layerManifest = inspection.Manifest with { Layers = [record] };
        var manifestBytes = ForgeProjectPersistence.Serialize(layerManifest);
        return await staging.CommitAsync(plan, async (output, token) =>
        {
            await ForgeProjectPersistence.WriteFileSafelyAsync(
                Path.Combine(output, "manifest.json"),
                manifestBytes,
                token);
            foreach (var asset in record.Assets)
            {
                var source = workspace.ResolveAssetPath(asset.Asset.Id, catalog)
                    ?? throw new FileNotFoundException($"Base-layer asset {asset.Asset.Id} disappeared during bake.");
                await CopyFileAsync(source, Path.Combine(output, asset.Name), token);
            }
        }, async (output, token) =>
        {
            var stagedManifest = await File.ReadAllBytesAsync(Path.Combine(output, "manifest.json"), token);
            if (!stagedManifest.SequenceEqual(manifestBytes))
                throw new InvalidDataException($"{plan.Layer} staged manifest changed during write.");
            foreach (var asset in record.Assets)
            {
                var bytes = await File.ReadAllBytesAsync(Path.Combine(output, asset.Name), token);
                ValidatePayload(plan.Layer, asset.Name, bytes);
                if (AssetId.Compute(asset.Asset.Kind, asset.CanonicalFormatVersion, bytes) != asset.Asset.Id)
                    throw new InvalidDataException($"{plan.Layer} staged asset {asset.Name} failed identity validation.");
            }
        }, cancellationToken);
    }

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
            case BakeLayerId.Collision when bytes.Length == 0:
                throw new InvalidDataException("UYA collision payload is empty.");
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
