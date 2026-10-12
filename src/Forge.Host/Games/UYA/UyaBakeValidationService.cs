using Forge.Host.Domain;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.UYA;

public static class UyaBakeValidationService
{
    public const string EmptyLightingWarning = "UYA_LIGHTING_EMPTY";

    public static async Task<BakeValidationResult> PreflightAsync(
        string projectRoot,
        AssetCatalogStore catalog,
        BakeFingerprintContext context,
        IReadOnlySet<string>? acknowledgedWarnings = null,
        bool rebuildAll = false,
        CancellationToken cancellationToken = default)
        => (await PrepareAsync(projectRoot, catalog, context, acknowledgedWarnings,
            rebuildAll, cancellationToken)).Validation;

    internal static Task<UyaBakePreparation> PrepareAsync(
        string projectRoot,
        AssetCatalogStore catalog,
        BakeFingerprintContext context,
        IReadOnlySet<string>? acknowledgedWarnings,
        bool rebuildAll,
        CancellationToken cancellationToken) => PrepareAsync(
            projectRoot, catalog, context, acknowledgedWarnings, rebuildAll, null, cancellationToken);

    internal static async Task<UyaBakePreparation> PrepareAsync(
        string projectRoot,
        AssetCatalogStore catalog,
        BakeFingerprintContext context,
        IReadOnlySet<string>? acknowledgedWarnings,
        bool rebuildAll,
        IReadOnlySet<BakeLayerId>? includedLayers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(context);
        var workspace = await ForgeProjectWorkspace.OpenAsync(projectRoot, cancellationToken);
        var staging = await BakeStagingStore.OpenAsync(projectRoot, cancellationToken);
        var baseInspection = await UyaBaseLayerStore.InspectAsync(workspace, catalog, cancellationToken);
        var baseInputs = await UyaBaseLayerStore.CreateBakeInputsAsync(
            workspace, catalog, baseInspection, cancellationToken);
        var staticInputs = await UyaStaticLayerStore.CreateBakeInputsAsync(workspace, catalog, cancellationToken);
        var gameplay = await UyaGameplayLayerStore.CreateBakeInputAsync(workspace, cancellationToken);
        var hud = await UyaHudBakeService.CreateBakeInputAsync(workspace, cancellationToken);
        var fx = await UyaFxBakeService.CreateBakeInputAsync(workspace, cancellationToken);
        var opaque = await OpaqueContentStore.CreateBakeInputAsync(projectRoot, cancellationToken);
        var inputs = baseInputs.Concat(staticInputs).Append(gameplay).Append(opaque).Append(hud).Append(fx).ToList();
        var target = workspace.Manifest.Target;
        var fingerprintContext = context with { Target = target };
        var plan = BakeLayerGraph.CreatePlan(fingerprintContext, inputs, staging.Manifest, rebuildAll);
        IReadOnlyDictionary<string, byte[]>? tfragCompositions = null;
        var tfragPlan = plan.Layers.Single(value => value.Layer == BakeLayerId.Tfrags);
        if ((includedLayers?.Contains(BakeLayerId.Tfrags) ?? true)
            && tfragPlan.State is (BakeLayerState.Dirty or BakeLayerState.DependencyInvalidated))
        {
            var record = baseInspection.Manifest?.Layers.SingleOrDefault(value => value.Layer == BakeLayerId.Tfrags);
            if (record is not null)
            {
                try
                {
                    tfragCompositions = await UyaTfragBakeService.ComposeAsync(
                        workspace, catalog, record, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (exception is InvalidDataException or IOException
                    or UnauthorizedAccessException or OverflowException or NotSupportedException)
                {
                    var index = inputs.FindIndex(value => value.Id == BakeLayerId.Tfrags);
                    var input = inputs[index];
                    inputs[index] = input with
                    {
                        Blockers = (input.Blockers ?? []).Append(
                            $"Tfrag composition is invalid ({exception.Message}); "
                            + "correct the identified piece transform and retry.").ToArray(),
                    };
                    plan = BakeLayerGraph.CreatePlan(fingerprintContext, inputs, staging.Manifest, rebuildAll);
                }
            }
        }
        var warnings = new List<BakeDiagnostic>();
        var lighting = inputs.Single(value => value.Id == BakeLayerId.Lighting);
        if (lighting.AssetIds.Count == 0)
            warnings.Add(new(
                EmptyLightingWarning,
                BakeDiagnosticSeverity.Warning,
                BakeLayerId.Lighting,
                null,
                null,
                "The source level has no native lighting payloads; Forge will bake the supported no-light fallback.",
                "Acknowledge this warning to continue, or restore lighting from the source level."));
        return new(
            Validate(target, plan, workspace.Content.Entities, warnings, acknowledgedWarnings),
            workspace.CurrentFingerprint,
            tfragCompositions);
    }

    public static BakeValidationResult Validate(
        ProjectTargetProfile target,
        BakePlan plan,
        IReadOnlyList<ProjectEntity> entities,
        IReadOnlyList<BakeDiagnostic>? warnings = null,
        IReadOnlySet<string>? acknowledgedWarnings = null)
    {
        var diagnostics = CreateDiagnostics(target, plan, entities)
            .Concat(warnings ?? [])
            .ToArray();
        var unacknowledged = diagnostics
            .Where(value => value.Severity == BakeDiagnosticSeverity.Warning
                && !(acknowledgedWarnings?.Contains(value.Code) ?? false))
            .Select(value => value.Code)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return new(plan, diagnostics, unacknowledged);
    }

    internal static IReadOnlyList<BakeDiagnostic> CreateDiagnostics(
        ProjectTargetProfile target,
        BakePlan plan,
        IReadOnlyList<ProjectEntity> entities)
    {
        var result = new List<BakeDiagnostic>();
        if (!LevelArchiveBuilder.SupportsTarget(target.Game, target.Region, target.Revision, target.BakeProfile))
            result.Add(new(
                "UYA_TARGET_UNSUPPORTED",
                BakeDiagnosticSeverity.Error,
                null,
                null,
                null,
                $"The SDK does not support target {target.Game} {target.Region} {target.Revision} ({target.BakeProfile}).",
                "Select a target declared by the installed Ratchet PS2 SDK."));
        foreach (var layer in plan.Layers)
        {
            for (var index = 0; index < layer.Blockers.Count; index++)
            {
                var blocker = layer.Blockers[index];
                var entity = entities.FirstOrDefault(value => blocker.Contains(
                    value.EntityId.ToString(), StringComparison.Ordinal));
                var asset = entity?.Asset?.Id ?? entities.Select(value => value.Asset?.Id)
                    .OfType<AssetId>()
                    .FirstOrDefault(value => blocker.Contains(value.ToString(), StringComparison.Ordinal));
                var (cause, action) = SplitAction(blocker, layer.Layer);
                result.Add(new(
                    $"UYA_{layer.Layer.ToString().ToUpperInvariant()}_BLOCKED_{index + 1:D2}",
                    BakeDiagnosticSeverity.Error,
                    layer.Layer,
                    entity?.EntityId,
                    asset == default ? null : asset,
                    cause,
                    action));
            }
        }
        return result;
    }

    private static (string Cause, string Action) SplitAction(string blocker, BakeLayerId layer)
    {
        var separator = blocker.LastIndexOf(';');
        if (separator < 0)
            return (blocker, $"Correct the {layer.ToString().ToLowerInvariant()} input and retry the bake.");
        return (blocker[..separator].TrimEnd('.'), blocker[(separator + 1)..].Trim());
    }
}

internal sealed record UyaBakePreparation(
    BakeValidationResult Validation,
    string ProjectFingerprint,
    IReadOnlyDictionary<string, byte[]>? TfragCompositions);
