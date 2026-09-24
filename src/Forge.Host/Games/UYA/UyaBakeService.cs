using Forge.Host.Domain;

namespace Forge.Host.Games.UYA;

public static class UyaBakeService
{
    public static async Task<UyaBakeResult> BakeAsync(
        string projectRoot,
        AssetCatalogStore catalog,
        BakeFingerprintContext context,
        IReadOnlySet<string>? acknowledgedWarnings = null,
        bool rebuildAll = false,
        IReadOnlySet<BakeLayerId>? includedLayers = null,
        Func<UyaBakeProgress, ValueTask>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await ReportAsync(progress, new(UyaBakePhase.Preflight, null, 0, 0, "Validating bake inputs."));
        var validation = await UyaBakeValidationService.PreflightAsync(
            projectRoot, catalog, context, acknowledgedWarnings, rebuildAll, cancellationToken);
        var staging = await BakeStagingStore.OpenAsync(projectRoot, cancellationToken);
        if (!validation.CanBake)
            return new(false, false, validation, staging.Manifest, [], staging.Manifest.PaletteReport);

        var pending = validation.Plan.Layers
            .Where(value => value.State is BakeLayerState.Dirty or BakeLayerState.DependencyInvalidated)
            .Where(value => includedLayers?.Contains(value.Layer) ?? true)
            .ToArray();
        var before = staging.Manifest;
        var written = new List<BakeLayerSnapshot>(pending.Length);
        try
        {
            for (var index = 0; index < pending.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var layer = pending[index];
                await ReportAsync(progress, new(
                    UyaBakePhase.Staging, layer.Layer, index, pending.Length, $"Baking {layer.Layer}."));
                written.Add(await StageAsync(projectRoot, catalog, staging, layer, cancellationToken));
                await ReportAsync(progress, new(
                    UyaBakePhase.Staging, layer.Layer, index + 1, pending.Length, $"Baked {layer.Layer}."));
            }

            var inventory = await UyaTextureInventoryService.BuildAsync(projectRoot, catalog, cancellationToken);
            var paletteReport = await Task.Run(
                () => PaletteBakeReportService.Create(inventory, cancellationToken), cancellationToken);
            if (staging.Manifest.PaletteReport is null
                || !PaletteBakeReportService.Equivalent(staging.Manifest.PaletteReport, paletteReport))
                await staging.SetPaletteReportAsync(paletteReport, cancellationToken);

            await ReportAsync(progress, new(
                UyaBakePhase.Validate, null, pending.Length, pending.Length, "Validating staged bake."));
            var finalStore = await BakeStagingStore.OpenAsync(projectRoot, cancellationToken);
            var finalValidation = await UyaBakeValidationService.PreflightAsync(
                projectRoot, catalog, context, acknowledgedWarnings, cancellationToken: cancellationToken);
            if (!finalValidation.CanBake
                || finalValidation.Plan.Layers.Any(value => value.State != BakeLayerState.Clean
                    && (includedLayers?.Contains(value.Layer) ?? true))
                || finalStore.Manifest.PaletteReport is null)
                throw new InvalidDataException("The completed UYA bake did not validate as current.");
            var allCurrent = finalValidation.Plan.Layers.All(value => value.State == BakeLayerState.Clean);
            await ReportAsync(progress, new(
                UyaBakePhase.Complete, null, pending.Length, pending.Length,
                pending.Length > 0 ? "Bake complete."
                    : allCurrent ? "Staging is current." : "Selected layers are current; unchecked changes remain deferred."));
            return new(
                true,
                allCurrent,
                finalValidation,
                finalStore.Manifest,
                written,
                finalStore.Manifest.PaletteReport);
        }
        catch
        {
            await staging.RestoreManifestAsync(before, CancellationToken.None);
            throw;
        }
    }

    private static Task<BakeLayerSnapshot> StageAsync(
        string projectRoot,
        AssetCatalogStore catalog,
        BakeStagingStore staging,
        BakeLayerPlan plan,
        CancellationToken cancellationToken) => plan.Layer switch
    {
        BakeLayerId.World or BakeLayerId.Sky or BakeLayerId.Tfrags
            or BakeLayerId.Collision or BakeLayerId.Lighting =>
            UyaBaseLayerStore.StageAsync(projectRoot, catalog, staging, plan, cancellationToken),
        BakeLayerId.Ties or BakeLayerId.Shrubs or BakeLayerId.Mobys =>
            UyaStaticLayerStore.StageAsync(projectRoot, catalog, staging, plan, cancellationToken),
        BakeLayerId.Gameplay => UyaGameplayLayerStore.StageAsync(projectRoot, staging, plan, cancellationToken),
        BakeLayerId.Opaque => OpaqueContentStore.StageAsync(projectRoot, staging, plan, cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(plan)),
    };

    private static ValueTask ReportAsync(
        Func<UyaBakeProgress, ValueTask>? progress,
        UyaBakeProgress value) => progress?.Invoke(value) ?? ValueTask.CompletedTask;
}
