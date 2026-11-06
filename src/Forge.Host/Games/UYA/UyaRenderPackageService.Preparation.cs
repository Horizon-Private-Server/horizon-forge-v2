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
        var octants = await ReadOcclusionOctantsAsync(project.Workspace, levelWad, cancellationToken);

        var packages = await Task.Run(() =>
        {
            return (
                Sky: FrontendMapPackageBuilder.BuildLevelWadPart(
                    levelWad, GameId.UYA, FrontendMapAssetGroup.Common),
                Environment: UyaRenderEnvironmentReader.Read(levelWad));
        }, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await ReportAsync(progress, 5_000, 10_000);

        var renderedAssets = await BuildAssetsAsync(project.Assets, progress, cancellationToken);
        await ReportAsync(progress, 8_000, 10_000);
        return new(terrain, packages.Sky, packages.Environment, octants, renderedAssets);
    }

    internal static async Task<IReadOnlyList<RenderOcclusionOctant>> ReadOcclusionOctantsAsync(
        ForgeProjectWorkspace workspace, byte[] levelWad, CancellationToken cancellationToken)
    {
        var imported = await UyaOcclusionImportPayload.ReadAsync(workspace, cancellationToken);
        return await Task.Run(() =>
        {
            var grid = imported is null ? UyaOcclusionGridReader.ReadLevelWad(levelWad)
                : UyaOcclusionGridReader.Read(imported.AssetGrid, 1_000_000, cancellationToken);
            return (IReadOnlyList<RenderOcclusionOctant>)grid.Octants.Select(value =>
                new RenderOcclusionOctant(value.X, value.Y, value.Z, value.MaskIndex)).ToArray();
        }, cancellationToken);
    }

    private sealed record PreparedProducts(
        PackedFilePackage Terrain,
        PackedFilePackage Sky,
        RenderEnvironment? Environment,
        IReadOnlyList<RenderOcclusionOctant> Octants,
        IReadOnlyList<BuiltAsset> Assets);
}
