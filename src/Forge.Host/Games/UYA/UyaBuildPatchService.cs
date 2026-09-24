using Forge.Host.Domain;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.Wad.Models;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.UYA;

public static class UyaBuildPatchService
{
    public static async Task<UyaBuildPlan> PlanAsync(
        string projectRoot,
        string catalogRoot,
        string hostVersion,
        string sdkRevision,
        CancellationToken cancellationToken = default)
    {
        var workspace = await ForgeProjectWorkspace.OpenAsync(projectRoot, cancellationToken);
        var catalog = await AssetCatalogStore.OpenAsync(catalogRoot, cancellationToken);
        var context = new BakeFingerprintContext(
            workspace.Manifest.Target,
            $"ratchet-sdk:{sdkRevision}",
            $"forge-host:{hostVersion}");
        var validation = await UyaBakeValidationService.PreflightAsync(
            projectRoot, catalog, context, cancellationToken: cancellationToken);
        var staging = await BakeStagingStore.OpenAsync(projectRoot, cancellationToken);
        var staged = staging.Manifest.Layers.Select(value => value.Layer).ToHashSet();
        return new(validation.Plan.Layers.Select(value => new UyaBuildLayerStatus(
            value.Layer, value.State, staged.Contains(value.Layer))).ToArray());
    }

    public static async Task<UyaBuildPatchResult> RunAsync(
        UyaBuildPatchRequest request,
        string hostVersion,
        string sdkRevision,
        Func<UyaBuildPatchProgress, ValueTask>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await RunCoreAsync(request, hostVersion, sdkRevision, progress, cancellationToken);
        }
        catch (OperationCanceledException exception)
        {
            var recovery = await RestoreInterruptedPatchAsync(request.DevelopmentIso, progress);
            throw new OperationCanceledException($"Build cancelled. {recovery}", exception, cancellationToken);
        }
    }

    private static async Task<UyaBuildPatchResult> RunCoreAsync(
        UyaBuildPatchRequest request,
        string hostVersion,
        string sdkRevision,
        Func<UyaBuildPatchProgress, ValueTask>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await ReportAsync(progress, new(UyaBuildPatchPhase.Preflight, 0, 1, "Validating build inputs."));
        var workspace = await ForgeProjectWorkspace.OpenAsync(request.ProjectRoot, cancellationToken);
        ValidateRequest(request, workspace);
        var catalog = await AssetCatalogStore.OpenAsync(request.CatalogRoot, cancellationToken);
        var context = new BakeFingerprintContext(
            workspace.Manifest.Target,
            $"ratchet-sdk:{sdkRevision}",
            $"forge-host:{hostVersion}");
        var sourceLevel = await ReadSourceLevelAsync(
            request.CleanSourceIso, workspace.Manifest.BaseLevel.Level, cancellationToken);

        var bake = await UyaBakeService.BakeAsync(
            request.ProjectRoot,
            catalog,
            context,
            request.AcknowledgedWarnings,
            includedLayers: request.IncludedLayers,
            progress: value => ReportAsync(progress, new(
                UyaBuildPatchPhase.Bake,
                value.CompletedLayers,
                Math.Max(1, value.TotalLayers),
                value.Message)),
            cancellationToken: cancellationToken);
        if (!bake.Succeeded)
            return Blocked(bake.Validation);

        var deferredLayers = bake.Validation.Plan.Layers
            .Where(value => value.State != BakeLayerState.Clean
                && !(request.IncludedLayers?.Contains(value.Layer) ?? true))
            .Select(value => value.Layer)
            .ToHashSet();

        await ReportAsync(progress, new(UyaBuildPatchPhase.Pack, 0, 1000, "Packing staged UYA level WAD."));
        var pack = await UyaLevelPackService.PackAsync(
            request.ProjectRoot,
            catalog,
            sourceLevel,
            context,
            request.AcknowledgedWarnings,
            deferredLayers,
            new InlineProgress<LevelArchiveProgress>(value => ReportAsync(progress, new(
                UyaBuildPatchPhase.Pack,
                Math.Clamp((long)Math.Round(value.Completion * 1000), 0, 1000),
                1000,
                value.Message)).AsTask().GetAwaiter().GetResult()),
            cancellationToken);
        if (!pack.Succeeded || pack.OutputBytes is null)
            return Blocked(pack.Diagnostics, "Packing failed.");

        await ReportAsync(progress, new(UyaBuildPatchPhase.Plan, 0, 1, "Planning development ISO patch."));
        var plan = await UyaIsoPatchService.PlanAsync(
            request.CleanSourceIso,
            request.DevelopmentIso,
            workspace.Manifest.BaseLevel.Level,
            pack.OutputBytes,
            new FileInfo(request.CleanSourceIso).Length,
            request.ForceFullImage,
            cancellationToken);
        await ReportAsync(progress, new(UyaBuildPatchPhase.Plan, 1, 1, plan.SdkPlan.StrategyReason));

        try
        {
            var patched = await UyaIsoPatchService.ApplyAsync(
                plan,
                value => ReportAsync(progress, new(
                    UyaBuildPatchPhase.Patch,
                    value.Completed,
                    Math.Max(1, value.Total),
                    value.Message)),
                cancellationToken);
            var replacedIso = patched.Mode == UyaIsoPatchMode.FullImageReplacement;
            var completionMessage = replacedIso
                ? "Development ISO replaced and verified. Start PCSX2 without loading a savestate."
                : "Development ISO patched and verified.";
            await ReportAsync(progress, new(
                UyaBuildPatchPhase.Complete, 1, 1, completionMessage));
            return new(
                true,
                false,
                [],
                Format(pack.Diagnostics),
                completionMessage,
                replacedIso
                    ? "Boot normally; existing savestates retain the previous ISO layout."
                    : "Reload the ISO in PCSX2 when ready.",
                patched.DevelopmentIsoPath,
                patched.Mode,
                patched.OutputLevelWadSha256,
                bake.WrittenLayers.Count,
                bake.IsCurrent);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or UnauthorizedAccessException or ArgumentException or OverflowException)
        {
            var recovery = await RestoreInterruptedPatchAsync(request.DevelopmentIso, progress);
            throw new IOException($"{exception.Message} {recovery}", exception);
        }
    }

    private static void ValidateRequest(UyaBuildPatchRequest request, ForgeProjectWorkspace workspace)
    {
        if (workspace.Manifest.Target is not { Game: "UYA", Region: "NTSC-U", Revision: "1.00" })
            throw new InvalidDataException("Build and patch currently supports only UYA NTSC-U revision 1.00 projects.");
        if (!workspace.Manifest.BaseLevel.SourceFingerprint.Equals(
                request.SourceFingerprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The configured clean ISO does not match the project's base source fingerprint.");
        if (workspace.MigrationPending)
            throw new InvalidOperationException("Migrate the project before building it.");
    }

    private static async Task<byte[]> ReadSourceLevelAsync(
        string sourceIso,
        int level,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            sourceIso, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        return await Task.Run(
            () => LevelArchiveReader.ExtractPrimary(GameId.UYA, stream, level),
            cancellationToken);
    }

    private static UyaBuildPatchResult Blocked(
        BakeValidationResult validation) => new(
        false,
        validation.UnacknowledgedWarnings.Count > 0
            && validation.Diagnostics.All(value => value.Severity != BakeDiagnosticSeverity.Error),
        validation.UnacknowledgedWarnings,
        Format(validation.Diagnostics),
        "Build preflight did not pass.",
        validation.UnacknowledgedWarnings.Count > 0
            ? "Review and acknowledge the warnings to continue."
            : "Correct the blocking diagnostics and retry.");

    private static UyaBuildPatchResult Blocked(
        IReadOnlyList<BakeDiagnostic> diagnostics,
        string message) => new(
        false,
        false,
        [],
        Format(diagnostics),
        message,
        "Correct the packing diagnostics and retry.");

    private static string[] Format(IEnumerable<BakeDiagnostic> diagnostics) => diagnostics
        .Select(value => $"{value.Code}: {value.Cause} {value.CorrectiveAction}")
        .ToArray();

    private static async Task<string> RestoreInterruptedPatchAsync(
        string developmentIso,
        Func<UyaBuildPatchProgress, ValueTask>? progress)
    {
        try
        {
            var recovery = await UyaIsoPatchService.InspectRecoveryAsync(developmentIso, CancellationToken.None);
            if (recovery is null) return "The development ISO was not changed.";
            await ReportAsync(progress, new(
                UyaBuildPatchPhase.Recovery, 0, 1, "Restoring the previous development ISO state."));
            await UyaIsoPatchService.RecoverAsync(developmentIso, CancellationToken.None);
            await ReportAsync(progress, new(
                UyaBuildPatchPhase.Recovery, 1, 1, "Previous development ISO state restored."));
            return "The previous development ISO state was restored.";
        }
        catch (Exception recoveryError)
        {
            return $"The recovery journal was retained; close PCSX2 and retry recovery. ({recoveryError.Message})";
        }
    }

    private static ValueTask ReportAsync(
        Func<UyaBuildPatchProgress, ValueTask>? progress,
        UyaBuildPatchProgress value) => progress?.Invoke(value) ?? ValueTask.CompletedTask;

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
