using Forge.Host.Domain;
using Forge.Host.Games.UYA;

namespace Forge.Host.ProtocolTests.Games.UYA;

internal static class UyaOcclusionCoverageBuildTests
{
    internal static async Task VerifyAsync(string project, AssetCatalogStore catalog,
        BakeFingerprintContext context, byte[] sourceWad, string? expectedHash)
    {
        var workspace = await ForgeProjectWorkspace.OpenAsync(project);
        var original = workspace.Content.OcclusionCoverage;
        var stageBefore = ForgeProjectPersistence.Serialize((await BakeStagingStore.OpenAsync(project)).Manifest);
        workspace.SetPrimaryOcclusionCoverageEnabled(false);
        await workspace.SaveAsync();
        var bake = await UyaBakeService.BakeAsync(project, catalog, context,
            includedLayers: new HashSet<BakeLayerId> { BakeLayerId.Ties });
        Check(!bake.Succeeded && bake.WrittenLayers.Count == 0
            && bake.Validation.Diagnostics.Any(d => d.Code == "UYA_OCCLUSION_COVERAGE_NOT_GENERATED"),
            "Disabled Primary must block baking, including selective bakes, without silently retaining its grid.");
        var packed = await UyaLevelPackService.PackAsync(project, catalog, sourceWad, context);
        Check(!packed.Succeeded && packed.OutputBytes is null
            && packed.Diagnostics.Any(d => d.Code == "UYA_OCCLUSION_COVERAGE_NOT_GENERATED"),
            "Packing must not fall back to Primary after clearing coverage.");

        var box = new ProjectOcclusionCoverageSource(EntityId.New(), "Custom coverage", true,
            ProjectOcclusionCoverageRole.Include, ProjectTransform.Identity, BoxSize: new(4, 4, 4));
        workspace.SetOcclusionCoverage(original with { Sources = [box] });
        await workspace.SaveAsync();
        packed = await UyaLevelPackService.PackAsync(project, catalog, sourceWad, context);
        Check(!packed.Succeeded && packed.Diagnostics.Any(d => d.Code == "UYA_OCCLUSION_COVERAGE_NOT_GENERATED"),
            "An enabled authored volume must block source-only packing even when Primary stays enabled.");

        workspace.SetOcclusionCoverage(original with { Sources = [box with { Enabled = false }] });
        await workspace.SaveAsync();
        packed = await UyaLevelPackService.PackAsync(project, catalog, sourceWad, context);
        Check(packed.Succeeded && packed.OutputSha256 == expectedHash,
            "Disabled authored helpers must preserve the original source-mode output.");
        workspace.SetOcclusionCoverage(original);
        await workspace.SaveAsync();
        var stageAfter = ForgeProjectPersistence.Serialize((await BakeStagingStore.OpenAsync(project)).Manifest);
        Check(stageBefore.SequenceEqual(stageAfter), "Rejected authored coverage must preserve the previous stage manifest.");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
