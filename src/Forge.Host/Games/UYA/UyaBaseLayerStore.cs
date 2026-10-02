using Forge.Host.Domain;
using System.Text.Json;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.Skyboxes;
using RatchetPs2.Core.Tfrags;
using RatchetPs2.Games.UYA.Gameplay;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.UYA;

public static class UyaBaseLayerStore
{
    public const string RelativeManifestPath = "content/uya-base-layers.json";
    private const string DirectionalLightsAssetName = "directional-lights.bin";
    private const string PointLightsAssetName = "point-lights.bin";
    private const string TieAmbientAssetName = "tie-ambient-rgbas.bin";
    private const int SkyCompositionVersion = 2;

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
        InspectAsync(projectRoot, catalog, null, cancellationToken);

    internal static Task<UyaBaseLayerInspection> InspectAsync(
        string projectRoot,
        AssetCatalogStore catalog,
        BakeLayerId layer,
        CancellationToken cancellationToken) =>
        InspectAsync(projectRoot, catalog, (BakeLayerId?)layer, cancellationToken);

    private static async Task<UyaBaseLayerInspection> InspectAsync(
        string projectRoot,
        AssetCatalogStore catalog,
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

        var workspace = await ForgeProjectWorkspace.OpenAsync(projectRoot, cancellationToken);
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
        var workspace = await ForgeProjectWorkspace.OpenAsync(projectRoot, cancellationToken);
        var blockers = inspection.Blockers.ToDictionary(pair => pair.Key, pair => pair.Value.ToList());
        if (blockers[BakeLayerId.Sky].Count == 0)
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
            var content = record is null || inspection.Manifest is null
                ? ReadOnlyMemory<byte>.Empty
                : ForgeProjectPersistence.Serialize(new UyaBaseLayerManifest(
                    inspection.Manifest.SchemaVersion,
                    inspection.Manifest.DocumentType,
                    inspection.Manifest.Source,
                    [record]));
            var assetIds = record?.Assets?.Select(value => value.Asset.Id) ?? [];
            if (layer == BakeLayerId.Sky)
                assetIds = assetIds.Concat(EnabledSkyShells(workspace)
                    .Where(value => value.Asset is not null).Select(value => value.Asset!.Id));
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
                    BakeLayerId.Collision => ForgeProjectPersistence.Serialize(new
                    {
                        CompositionVersion = 1,
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
                            }).ToArray(),
                    }),
                    BakeLayerId.Lighting => ForgeProjectPersistence.Serialize(new
                    {
                        TieAmbient = UyaStaticLayerStore.OrderedEntities(workspace, BakeLayerId.Ties)
                            .Select(value => new { value.EntityId, AmbientRgbas = value.TieLighting?.AmbientRgbas }).ToArray(),
                        DirectionalLights = DirectionalLights(workspace)
                            .Select(value => new { value.EntityId, value.Transform }).ToArray(),
                        PointLights = PointLights(workspace).Select(value => new { value.EntityId, value.Transform }).ToArray(),
                    }),
                    _ => ReadOnlyMemory<byte>.Empty,
                },
                blockers[layer]);
        }).ToArray();
    }

    public static Task<BakeLayerSnapshot> StageAsync(
        string projectRoot,
        AssetCatalogStore catalog,
        BakeStagingStore staging,
        BakeLayerPlan plan,
        CancellationToken cancellationToken = default) =>
        StageAsync(projectRoot, catalog, staging, plan, null, cancellationToken);

    internal static async Task<BakeLayerSnapshot> StageAsync(
        string projectRoot,
        AssetCatalogStore catalog,
        BakeStagingStore staging,
        BakeLayerPlan plan,
        Action<string>? fault,
        CancellationToken cancellationToken)
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
        var skyComposition = plan.Layer == BakeLayerId.Sky
            ? await UyaSkyShellEditorService.ComposeAsync(
                workspace, catalog, workspace.Content.Entities, inspection, cancellationToken)
            : null;
        var collisionCompositions = plan.Layer == BakeLayerId.Collision
            ? await ComposeCollisionAsync(workspace, catalog, record, cancellationToken)
            : null;
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
                if (plan.Layer == BakeLayerId.World && workspace.Content.LevelSettings is { } settings)
                {
                    var bytes = WriteLevelSettings(await File.ReadAllBytesAsync(source, token), settings);
                    await ForgeProjectPersistence.WriteFileSafelyAsync(Path.Combine(output, asset.Name), bytes, token);
                }
                else if (plan.Layer == BakeLayerId.Sky && asset.Name == "sky.bin")
                {
                    await ForgeProjectPersistence.WriteFileSafelyAsync(
                        Path.Combine(output, asset.Name), skyComposition!.Bytes, token);
                }
                else if (plan.Layer == BakeLayerId.Collision)
                {
                    await ForgeProjectPersistence.WriteFileSafelyAsync(
                        Path.Combine(output, asset.Name), collisionCompositions![asset.Name], token);
                }
                else if (plan.Layer == BakeLayerId.Lighting && asset.Name == PointLightsAssetName)
                {
                    await ForgeProjectPersistence.WriteFileSafelyAsync(
                        Path.Combine(output, asset.Name),
                        WritePointLights(await File.ReadAllBytesAsync(source, token), workspace), token);
                }
                else if (plan.Layer == BakeLayerId.Lighting && asset.Name == DirectionalLightsAssetName)
                {
                    await ForgeProjectPersistence.WriteFileSafelyAsync(
                        Path.Combine(output, asset.Name),
                        WriteDirectionalLights(await File.ReadAllBytesAsync(source, token), workspace), token);
                }
                else if (plan.Layer == BakeLayerId.Lighting && asset.Name == TieAmbientAssetName)
                {
                    await ForgeProjectPersistence.WriteFileSafelyAsync(
                        Path.Combine(output, asset.Name),
                        WriteTieAmbientRgbas(await File.ReadAllBytesAsync(source, token), workspace), token);
                }
                else
                {
                    await CopyFileAsync(source, Path.Combine(output, asset.Name), token);
                }
            }
        }, async (output, token) =>
        {
            fault?.Invoke(output);
            var stagedManifest = await File.ReadAllBytesAsync(Path.Combine(output, "manifest.json"), token);
            if (!stagedManifest.SequenceEqual(manifestBytes))
                throw new InvalidDataException($"{plan.Layer} staged manifest changed during write.");
            foreach (var asset in record.Assets)
            {
                var bytes = await File.ReadAllBytesAsync(Path.Combine(output, asset.Name), token);
                ValidatePayload(plan.Layer, asset.Name, bytes);
                if (plan.Layer == BakeLayerId.World && workspace.Content.LevelSettings is { } settings)
                {
                    var source = workspace.ResolveAssetPath(asset.Asset.Id, catalog)
                        ?? throw new FileNotFoundException($"Base-layer asset {asset.Asset.Id} disappeared during validation.");
                    var expected = WriteLevelSettings(await File.ReadAllBytesAsync(source, token), settings);
                    if (!bytes.SequenceEqual(expected))
                        throw new InvalidDataException("World staged level settings changed during write.");
                }
                else if (plan.Layer == BakeLayerId.Sky && asset.Name == "sky.bin")
                {
                    if (!bytes.SequenceEqual(skyComposition!.Bytes))
                        throw new InvalidDataException("Sky staged composition changed during write.");
                }
                else if (plan.Layer == BakeLayerId.Collision)
                {
                    if (!bytes.SequenceEqual(collisionCompositions![asset.Name]))
                        throw new InvalidDataException($"Collision staged composition {asset.Name} changed during write.");
                }
                else if (plan.Layer == BakeLayerId.Lighting && asset.Name == PointLightsAssetName)
                {
                    var source = workspace.ResolveAssetPath(asset.Asset.Id, catalog)
                        ?? throw new FileNotFoundException($"Base-layer asset {asset.Asset.Id} disappeared during validation.");
                    var expected = WritePointLights(await File.ReadAllBytesAsync(source, token), workspace);
                    if (!bytes.SequenceEqual(expected))
                        throw new InvalidDataException("Lighting staged point-light data changed during write.");
                }
                else if (plan.Layer == BakeLayerId.Lighting && asset.Name == DirectionalLightsAssetName)
                {
                    var source = workspace.ResolveAssetPath(asset.Asset.Id, catalog)
                        ?? throw new FileNotFoundException($"Base-layer asset {asset.Asset.Id} disappeared during validation.");
                    var expected = WriteDirectionalLights(await File.ReadAllBytesAsync(source, token), workspace);
                    if (!bytes.SequenceEqual(expected))
                        throw new InvalidDataException("Lighting staged directional-light data changed during write.");
                }
                else if (plan.Layer == BakeLayerId.Lighting && asset.Name == TieAmbientAssetName)
                {
                    var source = workspace.ResolveAssetPath(asset.Asset.Id, catalog)
                        ?? throw new FileNotFoundException($"Base-layer asset {asset.Asset.Id} disappeared during validation.");
                    var expected = WriteTieAmbientRgbas(await File.ReadAllBytesAsync(source, token), workspace);
                    if (!bytes.SequenceEqual(expected))
                        throw new InvalidDataException("Lighting staged tie ambient data changed during write.");
                }
                else if (AssetId.Compute(asset.Asset.Kind, asset.CanonicalFormatVersion, bytes) != asset.Asset.Id)
                    throw new InvalidDataException($"{plan.Layer} staged asset {asset.Name} failed identity validation.");
            }
        }, cancellationToken);
    }

    private static async Task<IReadOnlyDictionary<string, byte[]>> ComposeCollisionAsync(
        ForgeProjectWorkspace workspace,
        AssetCatalogStore catalog,
        UyaBaseLayerRecord record,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var asset in record.Assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = workspace.ResolveAssetPath(asset.Asset.Id, catalog)
                ?? throw new FileNotFoundException($"Collision source {asset.Asset.Id} is missing.");
            var source = await File.ReadAllBytesAsync(path, cancellationToken);
            var pieces = CollisionConverter.Inspect(source, GameId.UYA).Pieces;
            var sourceKeys = pieces.Select(value =>
                (UyaCollisionAdapter.ToProjectKind(value.Kind), value.SourcePieceIndex)).ToHashSet();
            var entities = workspace.Content.Entities
                .Where(value => value.Asset?.Id == asset.Asset.Id && value.Collision is not null)
                .ToDictionary(value => (value.Collision!.Kind, value.Collision.SourcePieceIndex));
            var edits = pieces.Select(piece =>
            {
                var kind = UyaCollisionAdapter.ToProjectKind(piece.Kind);
                if (!entities.TryGetValue((kind, piece.SourcePieceIndex), out var entity)
                    || entity.State?.Disabled == true)
                    return new CollisionPieceEdit(piece.Kind, piece.SourcePieceIndex, 0, 0, 0, Remove: true);
                return new CollisionPieceEdit(
                    piece.Kind,
                    piece.SourcePieceIndex,
                    entity.Transform.Position.X,
                    entity.Transform.Position.Y,
                    entity.Transform.Position.Z);
            }).ToArray();
            if (entities.Keys.Any(key => !sourceKeys.Contains(key)))
                throw new InvalidDataException($"Collision asset {asset.Name} contains an unknown project piece.");
            var composition = await Task.Run(
                () => CollisionConverter.Compose(source, GameId.UYA, edits), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(asset.Name, composition.Bytes);
        }
        return result;
    }

    private static byte[] WriteLevelSettings(byte[] source, ProjectLevelSettings settings) =>
        UyaLevelSettingsWriter.Write(source, new(
            new(settings.BackgroundColor.R, settings.BackgroundColor.G, settings.BackgroundColor.B),
            new(settings.FogColor.R, settings.FogColor.G, settings.FogColor.B),
            settings.FogNearDistance * 1024,
            settings.FogFarDistance * 1024,
            settings.FogNearIntensity,
            settings.FogFarIntensity));

    private static byte[] WriteTieAmbientRgbas(byte[] source, ForgeProjectWorkspace workspace)
    {
        var values = UyaStaticLayerStore.OrderedEntities(workspace, BakeLayerId.Ties)
            .Select(value => value.TieLighting?.AmbientRgbas
                ?? throw new InvalidDataException($"Tie {value.EntityId} has no ambient lighting data."))
            .ToArray();
        try
        {
            var original = UyaGameplayLightingReader.ReadTieAmbientRgbas(source, values.Length);
            if (original.Zip(values).All(pair => pair.First.SequenceEqual(pair.Second))) return source;
        }
        catch (InvalidDataException)
        {
            // A removed or reordered tie can make the source indices invalid; rebuild them below.
        }
        return UyaTieAmbientRgbasWriter.Write(values);
    }

    private static byte[] WritePointLights(byte[] source, ForgeProjectWorkspace workspace)
    {
        var table = UyaGameplayLightingReader.ReadPointLights(source);
        var entities = PointLights(workspace).ToArray();
        if (!entities.Select(value => value.Provenance!.SourceIndex)
                .SequenceEqual(Enumerable.Range(0, table.Lights.Length)))
            throw new InvalidDataException("Point lights have unsupported inserted, deleted, or reordered instances.");
        var edits = entities.Select((entity, index) => (entity, source: table.Lights[index]))
            .Where(value => !EquivalentPointLightTransform(value.entity.Transform, value.source))
            .Select(value =>
            {
                var transform = value.entity.Transform;
                if (!IdentityRotation(transform.Rotation)
                    || !Near(transform.Scale.X, transform.Scale.Y)
                    || !Near(transform.Scale.X, transform.Scale.Z))
                    throw new InvalidDataException($"Point light {value.entity.EntityId} requires identity rotation and uniform scale.");
                return new UyaPointLightEdit(value.entity.Provenance!.SourceIndex,
                    new(transform.Position.X, transform.Position.Y, transform.Position.Z), transform.Scale.X);
            }).ToArray();
        return UyaPointLightsWriter.Write(source, edits);
    }

    private static byte[] WriteDirectionalLights(byte[] source, ForgeProjectWorkspace workspace)
    {
        var lights = UyaGameplayLightingReader.ReadDirectionalLights(source);
        var entities = DirectionalLights(workspace).ToArray();
        if (!entities.Select(value => value.Provenance!.SourceIndex)
                .SequenceEqual(Enumerable.Range(0, lights.Length)))
            throw new InvalidDataException("Directional lights have unsupported inserted, deleted, or reordered instances.");
        return UyaDirectionalLightsWriter.Write(source, entities
            .Where(value => !IdentityRotation(value.Transform.Rotation))
            .Select(value => new UyaDirectionalLightEdit(value.Provenance!.SourceIndex,
                new(value.Transform.Rotation.X, value.Transform.Rotation.Y,
                    value.Transform.Rotation.Z, value.Transform.Rotation.W))).ToArray());
    }

    private static IEnumerable<ProjectEntity> DirectionalLights(ForgeProjectWorkspace workspace) =>
        workspace.Content.Entities.Where(value => value.Lighting?.DirectionalLight is not null)
            .OrderBy(value => value.Provenance?.SourceIndex ?? int.MaxValue);

    private static IEnumerable<ProjectEntity> PointLights(ForgeProjectWorkspace workspace) =>
        workspace.Content.Entities.Where(value => value.Lighting?.PointLight is not null)
            .OrderBy(value => value.Provenance?.SourceIndex ?? int.MaxValue);

    private static IEnumerable<ProjectEntity> EnabledSkyShells(ForgeProjectWorkspace workspace) =>
        workspace.Content.Entities.Where(value => value.SkyShell is not null && value.State?.Disabled != true)
            .OrderBy(value => value.SkyShell!.Order);

    private static bool EquivalentPointLightTransform(ProjectTransform transform, UyaPointLight source)
    {
        var radius = source.Radius > 0 ? source.Radius : 1;
        return Near(transform.Position.X, source.Position.X) && Near(transform.Position.Y, source.Position.Y)
            && Near(transform.Position.Z, source.Position.Z) && IdentityRotation(transform.Rotation)
            && Near(transform.Scale.X, radius) && Near(transform.Scale.Y, radius) && Near(transform.Scale.Z, radius);
    }

    private static bool IdentityRotation(ProjectQuaternion value) => Near(value.X, 0) && Near(value.Y, 0)
        && Near(value.Z, 0) && Near(MathF.Abs(value.W), 1);

    private static bool Near(float left, float right) => MathF.Abs(left - right) <= 0.00001f;

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
