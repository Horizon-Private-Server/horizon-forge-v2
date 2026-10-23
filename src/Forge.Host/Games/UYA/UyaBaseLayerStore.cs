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
    public const string RelativeManifestPath = "content/uya-base-layers.json";
    private const string DirectionalLightsAssetName = "directional-lights.bin";
    private const string PointLightsAssetName = "point-lights.bin";
    private const string TieAmbientAssetName = "tie-ambient-rgbas.bin";
    private const int SkyCompositionVersion = 2;
    private const int CollisionCompositionVersion = 9;

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

    public static Task<UyaBaseLayerInspection> InspectAsync(
        string projectRoot,
        AssetCatalogStore catalog,
        CancellationToken cancellationToken = default) =>
        InspectAsync(projectRoot, catalog, null, null, cancellationToken);

    internal static Task<UyaBaseLayerInspection> InspectAsync(
        string projectRoot,
        AssetCatalogStore catalog,
        BakeLayerId layer,
        CancellationToken cancellationToken) =>
        InspectAsync(projectRoot, catalog, null, layer, cancellationToken);

    internal static Task<UyaBaseLayerInspection> InspectAsync(
        ForgeProjectWorkspace workspace,
        AssetCatalogStore catalog,
        CancellationToken cancellationToken) =>
        InspectAsync(workspace.RootPath, catalog, workspace, null, cancellationToken);

    internal static Task<UyaBaseLayerInspection> InspectAsync(
        ForgeProjectWorkspace workspace,
        AssetCatalogStore catalog,
        BakeLayerId layer,
        CancellationToken cancellationToken) =>
        InspectAsync(workspace.RootPath, catalog, workspace, layer, cancellationToken);

    private static async Task<UyaBaseLayerInspection> InspectAsync(
        string projectRoot,
        AssetCatalogStore catalog,
        ForgeProjectWorkspace? workspace,
        BakeLayerId? requestedLayer,
        CancellationToken cancellationToken)
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

        workspace ??= await ForgeProjectWorkspace.OpenAsync(projectRoot, cancellationToken);
        ValidateManifest(manifest, workspace, blockers);
        foreach (var layer in manifest.Layers ?? [])
        {
            if (layer is null || !blockers.ContainsKey(layer.Layer)) continue;
            if (requestedLayer is not null && layer.Layer != requestedLayer) continue;
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
                var assetPath = workspace.ResolveAssetPath(asset.Asset, catalog);
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

    internal static Task<IReadOnlyList<UyaBaseLayerPayload>> ReadCollisionPayloadsAsync(
        ForgeProjectWorkspace workspace,
        AssetCatalogStore catalog,
        CancellationToken cancellationToken) => ReadSourcePayloadsAsync(
            workspace, catalog, BakeLayerId.Collision, CollisionSourceIndex, cancellationToken);

    internal static Task<IReadOnlyList<UyaBaseLayerPayload>> ReadTfragPayloadsAsync(
        ForgeProjectWorkspace workspace,
        AssetCatalogStore catalog,
        CancellationToken cancellationToken) => ReadSourcePayloadsAsync(
            workspace, catalog, BakeLayerId.Tfrags, TfragSourceIndex, cancellationToken);

    internal static async Task ValidateLevelSettingsAsync(
        ForgeProjectWorkspace workspace,
        AssetCatalogStore catalog,
        CancellationToken cancellationToken)
    {
        var settings = workspace.Content.LevelSettings
            ?? throw new InvalidDataException("Project level settings are missing.");
        ForgeProjectValidation.ValidateLevelSettings(settings, workspace.Content.Entities);
        var inspection = await InspectAsync(workspace, catalog, BakeLayerId.World, cancellationToken);
        if (inspection.Manifest is null || inspection.Blockers[BakeLayerId.World].Count > 0)
            throw new InvalidDataException(string.Join(' ', inspection.Blockers[BakeLayerId.World]));
        var asset = inspection.Manifest.Layers.Single(value => value.Layer == BakeLayerId.World)
            .Assets.Single(value => value.Name == "level-settings.bin");
        var source = workspace.ResolveAssetPath(asset.Asset, catalog)
            ?? throw new FileNotFoundException($"Base-layer asset {asset.Asset.Id} is missing.");
        var bytes = WriteLevelSettings(await File.ReadAllBytesAsync(source, cancellationToken), settings, workspace);
        ValidatePayload(BakeLayerId.World, asset.Name, bytes);
    }

    internal static async Task ValidateDirectionalLightsAsync(
        ForgeProjectWorkspace workspace,
        AssetCatalogStore catalog,
        CancellationToken cancellationToken)
    {
        var inspection = await InspectAsync(workspace, catalog, BakeLayerId.Lighting, cancellationToken);
        if (inspection.Manifest is null || inspection.Blockers[BakeLayerId.Lighting].Count > 0)
            throw new InvalidDataException(string.Join(' ', inspection.Blockers[BakeLayerId.Lighting]));
        var asset = inspection.Manifest.Layers.Single(value => value.Layer == BakeLayerId.Lighting)
            .Assets.Single(value => value.Name == DirectionalLightsAssetName);
        var source = workspace.ResolveAssetPath(asset.Asset, catalog)
            ?? throw new FileNotFoundException($"Base-layer asset {asset.Asset.Id} is missing.");
        var bytes = WriteDirectionalLights(await File.ReadAllBytesAsync(source, cancellationToken), workspace);
        ValidatePayload(BakeLayerId.Lighting, asset.Name, bytes);
    }

    internal static async Task ValidateCollisionAsync(
        ForgeProjectWorkspace workspace,
        AssetCatalogStore catalog,
        AssetId? replacementSourceAssetId,
        CancellationToken cancellationToken)
    {
        var inspection = await InspectAsync(workspace, catalog, BakeLayerId.Collision, cancellationToken);
        if (inspection.Manifest is null || inspection.Blockers[BakeLayerId.Collision].Count > 0)
            throw new InvalidDataException(string.Join(' ', inspection.Blockers[BakeLayerId.Collision]));
        var record = inspection.Manifest.Layers.Single(value => value.Layer == BakeLayerId.Collision);
        _ = await ComposeCollisionAsync(
            workspace, catalog, record, cancellationToken, replacementSourceAssetId);
    }

    private static async Task<IReadOnlyList<UyaBaseLayerPayload>> ReadSourcePayloadsAsync(
        ForgeProjectWorkspace workspace,
        AssetCatalogStore catalog,
        BakeLayerId layer,
        Func<string, int> sourceIndexFromName,
        CancellationToken cancellationToken)
    {
        var inspection = await InspectAsync(workspace, catalog, layer, cancellationToken);
        if (inspection.Manifest is null || inspection.Blockers[layer].Count > 0)
            throw new InvalidDataException(string.Join(" ", inspection.Blockers[layer]));
        var record = inspection.Manifest.Layers.Single(value => value.Layer == layer);
        var payloads = new List<UyaBaseLayerPayload>(record.Assets.Count);
        foreach (var asset in record.Assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = catalog.Query(new(Id: asset.Asset.Id)).Single();
            var sourceIndex = sourceIndexFromName(asset.Name);
            var source = entry.Sources.Single(value =>
                value.Game == inspection.Manifest.Source.Game
                && value.Region == inspection.Manifest.Source.Region
                && value.Revision == inspection.Manifest.Source.Revision
                && value.Level == $"level{inspection.Manifest.Source.Level:00}"
                && value.SourceIndex == sourceIndex
                && value.Fingerprint.Equals(inspection.Manifest.Source.Fingerprint, StringComparison.OrdinalIgnoreCase));
            var path = workspace.ResolveAssetPath(asset.Asset, catalog)
                ?? throw new FileNotFoundException($"Base-layer source {asset.Name} is missing.");
            payloads.Add(new(layer, asset.Name, asset.Asset.Kind, source.Archive, source.SourceIndex,
                await File.ReadAllBytesAsync(path, cancellationToken)));
        }
        return payloads;
    }

    public static async Task<IReadOnlyList<BakeLayerInput>> CreateBakeInputsAsync(
        string projectRoot,
        AssetCatalogStore catalog,
        CancellationToken cancellationToken = default)
    {
        var workspace = await ForgeProjectWorkspace.OpenAsync(projectRoot, cancellationToken);
        return await CreateBakeInputsAsync(workspace, catalog, cancellationToken);
    }

    internal static async Task<IReadOnlyList<BakeLayerInput>> CreateBakeInputsAsync(
        ForgeProjectWorkspace workspace,
        AssetCatalogStore catalog,
        CancellationToken cancellationToken)
    {
        var inspection = await InspectAsync(workspace, catalog, cancellationToken);
        return await CreateBakeInputsAsync(workspace, catalog, inspection, cancellationToken);
    }

    internal static async Task<IReadOnlyList<BakeLayerInput>> CreateBakeInputsAsync(
        ForgeProjectWorkspace workspace,
        AssetCatalogStore catalog,
        UyaBaseLayerInspection inspection,
        CancellationToken cancellationToken)
    {
        var blockers = inspection.Blockers.ToDictionary(pair => pair.Key, pair => pair.Value.ToList());
        var instancedCollisionSourceIds = workspace.Content.InstancedCollisionBindings
            .Select(binding => binding.SourceAssetId).ToHashSet();
        var skyRecord = inspection.Manifest?.Layers.SingleOrDefault(value => value.Layer == BakeLayerId.Sky);
        var hasSkyComposition = skyRecord?.Assets.Count > 0
            || workspace.Content.Entities.Any(value => value.SkyShell is not null);
        if (blockers[BakeLayerId.Sky].Count == 0 && hasSkyComposition)
        {
            try
            {
                _ = await UyaSkyShellEditorService.ComposeAsync(
                    workspace, catalog, workspace.Content.Entities, inspection, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException
                or UnauthorizedAccessException or OverflowException or NotSupportedException)
            {
                blockers[BakeLayerId.Sky].Add(
                    $"Sky composition is invalid ({exception.Message}); restore its source assets or correct the shell settings.");
            }
        }
        return UyaBaseLayerSchema.Layers.Select(layer =>
        {
            var record = inspection.Manifest?.Layers?.SingleOrDefault(value => value.Layer == layer);
            if (layer == BakeLayerId.Sky && record is not null
                && (record.Assets.Count > 0
                    || workspace.Content.Entities.Any(value => value.SkyShell is not null)))
                record = UyaSkyShellEditorService.EffectiveBaseLayerRecord(
                    inspection, workspace.Content.Entities);
            var content = record is null || inspection.Manifest is null
                ? ReadOnlyMemory<byte>.Empty
                : ForgeProjectPersistence.Serialize(new UyaBaseLayerManifest(
                    inspection.Manifest.SchemaVersion,
                    inspection.Manifest.DocumentType,
                    inspection.Manifest.Source,
                    [record]));
            var assetIds = record?.Assets?.Select(value => workspace.ResolveAssetReference(value.Asset).Id) ?? [];
            if (layer == BakeLayerId.Sky)
                assetIds = assetIds.Concat(EnabledSkyShells(workspace)
                    .Where(value => value.Asset is not null)
                    .Select(value => workspace.ResolveAssetReference(value.Asset!).Id));
            if (layer == BakeLayerId.Tfrags)
                assetIds = assetIds.Concat(UyaTfragBakeService.AssetIds(workspace));
            if (layer == BakeLayerId.Collision)
                assetIds = assetIds.Concat(workspace.Content.InstancedCollisionBindings
                    .SelectMany(value => new[] { value.SourceAssetId, value.ProxyAssetId }));
            return new BakeLayerInput(
                layer,
                content,
                assetIds.ToArray(),
                layer switch
                {
                    BakeLayerId.World when workspace.Content.LevelSettings is not null =>
                        ForgeProjectPersistence.Serialize(workspace.Content.LevelSettings),
                    BakeLayerId.Sky => ForgeProjectPersistence.Serialize(new
                    {
                        CompositionVersion = SkyCompositionVersion,
                        Shells = EnabledSkyShells(workspace).Select(value => new
                        {
                            AssetId = value.Asset?.Id,
                            value.SkyShell!.SourceShellIndex,
                            value.SkyShell.InitialRotationRadians,
                            value.SkyShell.AngularVelocityRadiansPerSecond,
                        }).ToArray(),
                    }),
                    BakeLayerId.Tfrags => UyaTfragBakeService.CreateFingerprintSettings(workspace),
                    BakeLayerId.Collision => ForgeProjectPersistence.Serialize(new
                    {
                        CompositionVersion = CollisionCompositionVersion,
                        Pieces = workspace.Content.Entities.Where(value => value.Collision is not null)
                            .OrderBy(value => value.Asset!.Id.ToString(), StringComparer.Ordinal)
                            .ThenBy(value => value.Collision!.Kind)
                            .ThenBy(value => value.Collision!.SourcePieceIndex)
                            .Select(value => new
                            {
                                AssetId = value.Asset!.Id,
                                value.Collision!.Kind,
                                value.Collision.SourcePieceIndex,
                                Enabled = value.State?.Disabled != true,
                                value.Transform.Position,
                                value.Transform.Rotation,
                            }).ToArray(),
                        Bindings = workspace.Content.InstancedCollisionBindings
                            .OrderBy(value => value.SourceAssetId.ToString(), StringComparer.Ordinal)
                            .ThenBy(value => value.InstanceEntityId?.ToString(), StringComparer.Ordinal)
                            .Select(value => new
                            {
                                value.SourceAssetId,
                                value.InstanceEntityId,
                                value.ProxyAssetId,
                                value.Recipe,
                                value.FaceTypeOverrides,
                            }).ToArray(),
                        Instances = workspace.Content.Entities.Where(value =>
                                value.Asset.IsInstancedCollisionSource()
                                && instancedCollisionSourceIds.Contains(value.Asset.Id))
                            .OrderBy(value => value.EntityId.ToString(), StringComparer.Ordinal)
                            .Select(value => new
                            {
                                value.EntityId,
                                SourceAssetId = value.Asset!.Id,
                                Mode = value.State?.Disabled == true ? null : value.InstancedCollisionEnabled,
                                value.Transform,
                            }).ToArray(),
                    }),
                    BakeLayerId.Lighting => ForgeProjectPersistence.Serialize(new
                    {
                        TieAmbient = UyaStaticLayerStore.OrderedEntities(workspace, BakeLayerId.Ties)
                            .Select(value => new { value.EntityId, AmbientRgbas = value.TieLighting?.AmbientRgbas }).ToArray(),
                        DirectionalLights = DirectionalLights(workspace)
                            .Select(value => new
                            {
                                value.EntityId,
                                value.Transform,
                                value.Lighting!.DirectionalLight,
                            }).ToArray(),
                        PointLights = PointLights(workspace).Select(value => new { value.EntityId, value.Transform }).ToArray(),
                    }),
                    _ => ReadOnlyMemory<byte>.Empty,
                },
                blockers[layer]);
        }).ToArray();
    }
}
