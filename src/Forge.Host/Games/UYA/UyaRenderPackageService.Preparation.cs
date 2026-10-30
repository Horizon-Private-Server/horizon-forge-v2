using RatchetPs2.Games.UYA.Occlusion;
using Forge.Host.Domain;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.Wad.Models;
using RatchetPs2.Games.UYA.Level;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.UYA;

public static partial class UyaRenderPackageService
{
    private static async Task<PreparedProducts> PrepareProductsAsync(
        FileStream iso,
        int level,
        ResolvedProject project,
        Func<IsoProgress, ValueTask>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await ReportAsync(progress, 2_000, 10_000);

        var levelWad = await Task.Run(
            () => LevelArchiveReader.ExtractPrimary(GameId.UYA, iso, level),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        await ReportAsync(progress, 3_000, 10_000);

        var terrain = await UyaCustomTfragRenderService.BuildAsync(
            levelWad, level, project.Workspace, project.Catalog, cancellationToken);

        var packages = await Task.Run(() =>
        {
            var octants = UyaOcclusionGridReader.ReadLevelWad(levelWad).Octants.Select(value =>
                new RenderOcclusionOctant(value.X, value.Y, value.Z, value.MaskIndex)).ToArray();
            return (
                Sky: FrontendMapPackageBuilder.BuildLevelWadPart(
                    levelWad, GameId.UYA, FrontendMapAssetGroup.Common),
                Environment: UyaRenderEnvironmentReader.Read(levelWad),
                Octants: (IReadOnlyList<RenderOcclusionOctant>)octants);
        }, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await ReportAsync(progress, 5_000, 10_000);

        var renderedAssets = await BuildAssetsAsync(project.Assets, progress, cancellationToken);
        await ReportAsync(progress, 8_000, 10_000);
        return new(terrain, packages.Sky, packages.Environment, packages.Octants, renderedAssets);
    }

    private sealed record PreparedProducts(
        PackedFilePackage Terrain,
        PackedFilePackage Sky,
        RenderEnvironment? Environment,
        IReadOnlyList<RenderOcclusionOctant> Octants,
        IReadOnlyList<BuiltAsset> Assets);
}
