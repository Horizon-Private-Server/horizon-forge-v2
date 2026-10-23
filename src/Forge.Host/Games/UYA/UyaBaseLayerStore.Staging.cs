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
    public static Task<BakeLayerSnapshot> StageAsync(
        string projectRoot,
        AssetCatalogStore catalog,
        BakeStagingStore staging,
        BakeLayerPlan plan,
        CancellationToken cancellationToken = default) =>
        StageAsync(projectRoot, catalog, staging, plan, null, null, cancellationToken);

    internal static Task<BakeLayerSnapshot> StageAsync(
        string projectRoot,
        AssetCatalogStore catalog,
        BakeStagingStore staging,
        BakeLayerPlan plan,
        UyaBakePreparation? preparation,
        CancellationToken cancellationToken) =>
        StageAsync(projectRoot, catalog, staging, plan, preparation, null, cancellationToken);

    internal static Task<BakeLayerSnapshot> StageAsync(
        string projectRoot,
        AssetCatalogStore catalog,
        BakeStagingStore staging,
        BakeLayerPlan plan,
        Action<string>? fault,
        CancellationToken cancellationToken) =>
        StageAsync(projectRoot, catalog, staging, plan, null, fault, cancellationToken);

    private static async Task<BakeLayerSnapshot> StageAsync(
        string projectRoot,
        AssetCatalogStore catalog,
        BakeStagingStore staging,
        BakeLayerPlan plan,
        UyaBakePreparation? preparation,
        Action<string>? fault,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(staging);
        ArgumentNullException.ThrowIfNull(plan);
        if (!UyaBaseLayerSchema.Layers.Contains(plan.Layer))
            throw new ArgumentException("Plan is not a UYA base layer.", nameof(plan));
        var workspace = await ForgeProjectWorkspace.OpenAsync(projectRoot, cancellationToken);
        if (preparation is not null && preparation.ProjectFingerprint != workspace.CurrentFingerprint)
            throw new InvalidDataException("Project content changed after bake preflight.");
        var inspection = await InspectAsync(workspace, catalog, plan.Layer, cancellationToken);
        var blockers = inspection.Blockers[plan.Layer];
        if (blockers.Count > 0) throw new InvalidDataException(string.Join(' ', blockers));
        var record = inspection.Manifest!.Layers.Single(value => value.Layer == plan.Layer);
        if (plan.Layer == BakeLayerId.Sky)
            record = UyaSkyShellEditorService.EffectiveBaseLayerRecord(
                inspection, workspace.Content.Entities);
        var layerManifest = inspection.Manifest with { Layers = [record] };
        var manifestBytes = ForgeProjectPersistence.Serialize(layerManifest);
        var skyComposition = plan.Layer == BakeLayerId.Sky
            ? await UyaSkyShellEditorService.ComposeAsync(
                workspace, catalog, workspace.Content.Entities, inspection, cancellationToken)
            : null;
        var collisionCompositions = plan.Layer == BakeLayerId.Collision
            ? await ComposeCollisionAsync(workspace, catalog, record, cancellationToken)
            : null;
        if (plan.Layer == BakeLayerId.Tfrags && preparation?.TfragCompositions is not null)
            await UyaTfragBakeService.ValidateEditedAssetsAsync(workspace, cancellationToken);
        var tfragCompositions = plan.Layer == BakeLayerId.Tfrags
            ? preparation?.TfragCompositions
                ?? await UyaTfragBakeService.ComposeAsync(workspace, catalog, record, cancellationToken)
            : null;
        var expectedFiles = record.Assets.Select(value => value.Name)
            .Append("manifest.json")
            .ToHashSet(StringComparer.Ordinal);
        return await staging.CommitFilesAsync(plan, async (output, token) =>
        {
            await ForgeProjectPersistence.WriteFileSafelyAsync(
                Path.Combine(output, "manifest.json"),
                manifestBytes,
                token);
            foreach (var asset in record.Assets)
            {
                var source = workspace.ResolveAssetPath(asset.Asset, catalog)
                    ?? throw new FileNotFoundException($"Base-layer asset {asset.Asset.Id} disappeared during bake.");
                if (plan.Layer == BakeLayerId.World && workspace.Content.LevelSettings is { } settings)
                {
                    var bytes = WriteLevelSettings(await File.ReadAllBytesAsync(source, token), settings, workspace);
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
                else if (plan.Layer == BakeLayerId.Tfrags
                    && tfragCompositions!.TryGetValue(asset.Name, out var tfragComposition))
                {
                    await ForgeProjectPersistence.WriteFileSafelyAsync(
                        Path.Combine(output, asset.Name), tfragComposition, token);
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
            fault?.Invoke(output);
        }, expectedFiles, async (relativePath, bytes, token) =>
        {
            if (relativePath == "manifest.json")
            {
                if (!bytes.SequenceEqual(manifestBytes))
                    throw new InvalidDataException($"{plan.Layer} staged manifest changed during write.");
                return;
            }
            var asset = record.Assets.SingleOrDefault(value => value.Name == relativePath)
                ?? throw new InvalidDataException($"{plan.Layer} staged an unexpected file {relativePath}.");
            ValidatePayload(plan.Layer, asset.Name, bytes);
            if (plan.Layer == BakeLayerId.World && workspace.Content.LevelSettings is { } settings)
            {
                var source = workspace.ResolveAssetPath(asset.Asset, catalog)
                    ?? throw new FileNotFoundException($"Base-layer asset {asset.Asset.Id} disappeared during validation.");
                var expected = WriteLevelSettings(await File.ReadAllBytesAsync(source, token), settings, workspace);
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
            else if (plan.Layer == BakeLayerId.Tfrags
                && tfragCompositions!.TryGetValue(asset.Name, out var tfragComposition))
            {
                if (!bytes.SequenceEqual(tfragComposition))
                    throw new InvalidDataException($"Tfrag staged composition {asset.Name} changed during write.");
            }
            else if (plan.Layer == BakeLayerId.Lighting && asset.Name == PointLightsAssetName)
            {
                var source = workspace.ResolveAssetPath(asset.Asset, catalog)
                    ?? throw new FileNotFoundException($"Base-layer asset {asset.Asset.Id} disappeared during validation.");
                var expected = WritePointLights(await File.ReadAllBytesAsync(source, token), workspace);
                if (!bytes.SequenceEqual(expected))
                    throw new InvalidDataException("Lighting staged point-light data changed during write.");
            }
            else if (plan.Layer == BakeLayerId.Lighting && asset.Name == DirectionalLightsAssetName)
            {
                var source = workspace.ResolveAssetPath(asset.Asset, catalog)
                    ?? throw new FileNotFoundException($"Base-layer asset {asset.Asset.Id} disappeared during validation.");
                var expected = WriteDirectionalLights(await File.ReadAllBytesAsync(source, token), workspace);
                if (!bytes.SequenceEqual(expected))
                    throw new InvalidDataException("Lighting staged directional-light data changed during write.");
            }
            else if (plan.Layer == BakeLayerId.Lighting && asset.Name == TieAmbientAssetName)
            {
                var source = workspace.ResolveAssetPath(asset.Asset, catalog)
                    ?? throw new FileNotFoundException($"Base-layer asset {asset.Asset.Id} disappeared during validation.");
                var expected = WriteTieAmbientRgbas(await File.ReadAllBytesAsync(source, token), workspace);
                if (!bytes.SequenceEqual(expected))
                    throw new InvalidDataException("Lighting staged tie ambient data changed during write.");
            }
            else
            {
                var resolved = workspace.ResolveAsset(asset.Asset, catalog)
                    ?? throw new FileNotFoundException(
                        $"Base-layer asset {asset.Asset.Id} disappeared during validation.");
                if (resolved.CanonicalFormatVersion != asset.CanonicalFormatVersion
                    || AssetId.Compute(asset.Asset.Kind, resolved.CanonicalFormatVersion, bytes)
                        != resolved.Effective.Id)
                    throw new InvalidDataException(
                        $"{plan.Layer} staged asset {asset.Name} failed identity validation.");
            }
        }, cancellationToken);
    }

}
