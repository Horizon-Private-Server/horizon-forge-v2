using System.Security.Cryptography;
using Forge.Host.Domain;

namespace Forge.Host.Games.UYA;

internal sealed record UyaOcclusionSceneInput(byte[] LevelWad, string ProjectFingerprint, string SceneFingerprint);

/// <summary>Composes the captured editor state in an isolated workspace, without touching user staging.</summary>
internal static class UyaOcclusionSceneSnapshot
{
    internal static async Task<UyaOcclusionSceneInput> CaptureAsync(
        ForgeProjectWorkspace projection, string catalogRoot, ReadOnlyMemory<byte> sourceLevelWad,
        Func<IsoProgress, ValueTask>? progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(projection);
        if (string.IsNullOrWhiteSpace(catalogRoot)) throw new InvalidOperationException("Graph preview needs the project's asset catalog. Reopen the project with its catalog.");
        if (sourceLevelWad.IsEmpty) throw new InvalidDataException("Graph preview needs the verified source level archive.");
        token.ThrowIfCancellationRequested();
        var fingerprint = projection.CurrentFingerprint;
        if (progress is not null) await progress(new(0, 3_000));
        // Coverage authoring does not affect scene geometry; only the disposable copy uses source-mode preflight.
        using var snapshot = await ForgeProjectSnapshot.CreateAsync(projection, projection.Content with
            { OcclusionCoverage = ProjectOcclusionCoverageState.PrimaryOnly }, token);
        var root = snapshot.RootPath;
        var manifest = projection.Manifest;
        var catalog = await AssetCatalogStore.OpenReadOnlyAsync(catalogRoot, token);
        var context = new BakeFingerprintContext(manifest.Target, "coverage-scene-preview-v1", "coverage-scene-preview-v1");
        // This is the existing optional fallback; unrelated unacknowledged warnings must still stop evaluation.
        var warnings = new HashSet<string> { UyaBakeValidationService.EmptyLightingWarning };
        if (progress is not null) await progress(new(1_000, 3_000));
        var bake = await UyaBakeService.BakeAsync(root, catalog, context, warnings,
            progress: progress is null ? null : p => progress(new(1_000 + p.CompletedLayers * 1_000 / Math.Max(1, p.TotalLayers), 3_000)),
            cancellationToken: token);
        if (!bake.Succeeded) throw Failure("prepare", bake.Validation.Diagnostics);
        if (progress is not null) await progress(new(2_000, 3_000));
        var pack = await UyaLevelPackService.PackAsync(root, catalog, sourceLevelWad, context, warnings,
            cancellationToken: token, applyGeneratedOcclusion: false);
        if (!pack.Succeeded || pack.OutputBytes is null) throw Failure("compose", pack.Diagnostics);
        token.ThrowIfCancellationRequested();
        var hash = Convert.ToHexStringLower(SHA256.HashData(pack.OutputBytes));
        if (progress is not null) await progress(new(3_000, 3_000));
        token.ThrowIfCancellationRequested();
        return new(pack.OutputBytes, fingerprint, hash);
    }

    private static InvalidDataException Failure(string phase, IReadOnlyList<BakeDiagnostic> diagnostics) => new(
        $"Could not {phase} the current graph-preview scene. " + string.Join(" ", diagnostics.Select(d => $"{d.Cause} {d.CorrectiveAction}")));
}
