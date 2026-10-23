using Forge.Host.Domain;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.LevelAssets;
using RatchetPs2.Core.Textures.Palettes;
using RatchetPs2.Games.UYA.Gameplay;
using RatchetPs2.Games.UYA.Level;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.UYA;

internal sealed record UyaStaticAssetPackResult(
    LevelAssetWadComposition FinalAssets,
    StaticAssetComposition Composition,
    bool AssetsChanged);

internal static class UyaStaticAssetPackService
{
    public static async Task<UyaStaticAssetPackResult> ComposeAsync(
        ForgeProjectWorkspace workspace,
        AssetCatalogStore catalog,
        BakeStagingStore staging,
        ReadOnlyMemory<byte> sourceLevelWad,
        UyaLevelWadPackage sourcePackage,
        ProjectBaseLevel baseLevel,
        UyaLevelAssetSourceFiles sourceAssets,
        LevelAssetWadComposition composed,
        IReadOnlyList<TerrainTextureInput> customTfragTextures,
        IDictionary<string, ReadOnlyMemory<byte>> replacements,
        bool allowStalePaletteReport,
        CancellationToken cancellationToken)
    {
        var staticAssets = new List<StaticAssetInput>();
        var sourceStaticAssetKeys = new HashSet<(TextureAssetFamily Family, int ClassId)>();
        var sourceLevel = $"level{baseLevel.Level:00}";
        var replacedFamilies = workspace.Content.ImportBatches
            .SelectMany(value => value.Families)
            .Where(value => value.Mode == ContentImportMode.Replace)
            .Select(value => value.Family switch
            {
                "ties" => TextureAssetFamily.Tie,
                "shrubs" => TextureAssetFamily.Shrub,
                "mobys" => TextureAssetFamily.Moby,
                _ => (TextureAssetFamily?)null,
            })
            .OfType<TextureAssetFamily>()
            .ToHashSet();
        var staticModelsChanged = replacedFamilies.Count > 0;
        foreach (var layer in UyaStaticLayerSchema.Layers)
        {
            var root = SnapshotRoot(staging, layer);
            var manifest = Read<UyaStaticBakeManifest>(root, "manifest.json", $"staged {layer} layer");
            var familyName = layer.ToString().ToLowerInvariant().TrimEnd('s');
            foreach (var definition in manifest.Definitions)
            {
                var entry = catalog.Query(new(Id: definition.Asset.Id)).SingleOrDefault();
                var attached = workspace.Content.Assets.Any(value => value.Id == definition.Asset.Id);
                var custom = layer == BakeLayerId.Ties
                    && workspace.Content.CustomModels.Models.Any(model => model.Canonical == definition.Asset);
                if (!custom && !attached && (entry is null
                    || entry.CanonicalFormatVersion != UyaAssetImportService.CanonicalFormatVersion))
                    throw new InvalidDataException(
                        $"{layer} definition {definition.ClassId:X4} has no current canonical asset.");
                var fromSourceLevel = !attached && entry?.Sources.Any(value =>
                    value.Game == baseLevel.Game
                    && value.Region == baseLevel.Region
                    && value.Revision == baseLevel.Revision
                    && value.Level == sourceLevel) == true;
                staticModelsChanged |= !fromSourceLevel;
                var path = ForgeProjectPersistence.ResolveRelativePath(root, definition.Resource);
                var canonical = UyaCanonicalAssetCodec.Decode(
                    await File.ReadAllBytesAsync(path, cancellationToken));
                var input = new StaticAssetInput(
                    definition.EffectiveAsset.Id.ToString(),
                    TextureFamily(layer),
                    definition.ClassId,
                    canonical.DefinitionBytes,
                    canonical.ModelBytes,
                    canonical.Textures.Select(value => new StaticAssetTexture(
                        value.Role == 0 ? TextureRole.Material : TextureRole.Billboard,
                        value.PifBytes)).ToArray(),
                    PreserveTextureIndexes: fromSourceLevel);
                staticAssets.Add(input);
                if (fromSourceLevel) sourceStaticAssetKeys.Add((input.Family, input.ClassId));
            }

            var classPath = $"gameplay/core/{familyName}_classes.bin";
            var requiredClasses = manifest.Instances.Select(value => value.ClassId);
            var sourceClasses = sourcePackage.Files.SingleOrDefault(value => value.Path == classPath);
            replacements.Add(classPath, sourceClasses is null
                ? UyaClassIdListWriter.Write(requiredClasses)
                : UyaClassIdListWriter.Compose(sourceClasses.Bytes, requiredClasses));
            replacements.Add($"gameplay/core/{familyName}_instances.bin",
                await File.ReadAllBytesAsync(Path.Combine(root, "instances.bin"), cancellationToken));
            if (layer is BakeLayerId.Ties or BakeLayerId.Shrubs or BakeLayerId.Mobys)
            {
                var referenceFiles = new List<(string Name, string Path)>
                {
                    ("groups.bin", $"gameplay/core/{familyName}_groups.bin"),
                };
                if (layer == BakeLayerId.Ties)
                    referenceFiles.Add(("occlusion.bin", "gameplay/core/occlusion.bin"));
                foreach (var (name, path) in referenceFiles)
                {
                    var staged = Path.Combine(root, name);
                    if (File.Exists(staged))
                        replacements.Add(path, await File.ReadAllBytesAsync(staged, cancellationToken));
                }
            }
        }

        if (staticModelsChanged)
        {
            staticAssets = IncludeSourceStaticAssets(
                sourceLevelWad.Span, staticAssets, sourceStaticAssetKeys, replacedFamilies).ToList();
        }
        var staticAssetsChanged = staticModelsChanged || customTfragTextures.Count > 0;
        // Preserve retail static model payloads unless a model actually changed. Full
        // recomposition is not byte-compatible with every retail class and can make the
        // game jump through corrupt scratchpad data during level initialization.
        var composition = staticModelsChanged
            ? StaticAssetComposer.Compose(
                GameId.UYA,
                composed.HeaderBytes,
                composed.AssetWadBytes,
                sourceAssets.PaletteBytes,
                staticAssets,
                customTfragTextures,
                cancellationToken)
            : customTfragTextures.Count > 0
                ? StaticAssetComposer.AppendTerrainTextures(
                    GameId.UYA,
                    composed.HeaderBytes,
                    composed.AssetWadBytes,
                    sourceAssets.PaletteBytes,
                    customTfragTextures,
                    cancellationToken)
                : StaticAssetComposer.Compose(
                    GameId.UYA,
                    composed.HeaderBytes,
                    composed.AssetWadBytes,
                    sourceAssets.PaletteBytes,
                    staticAssets,
                    cancellationToken);
        var actualPaletteReport = PaletteBakeReportService.Create(
            composition.Inventory, composition.Optimization);
        var stagedPaletteReport = staging.Manifest.PaletteReport
            ?? throw new InvalidDataException("Staged output is missing its palette report.");
        if (allowStalePaletteReport || staticAssetsChanged)
            await staging.SetPaletteReportAsync(actualPaletteReport, cancellationToken);
        else if (!PaletteBakeReportService.Equivalent(stagedPaletteReport, actualPaletteReport))
            throw new InvalidDataException(
                "Staged palette report does not match the generated asset payloads.");

        var finalAssets = staticAssetsChanged
            ? new LevelAssetWadComposition(composition.HeaderBytes, composition.AssetWadBytes)
            : composed;
        return new(finalAssets, composition, staticAssetsChanged);
    }

    internal static IReadOnlyList<StaticAssetInput> IncludeSourceStaticAssets(
        ReadOnlySpan<byte> sourceLevelWad,
        IReadOnlyList<StaticAssetInput> selected,
        IReadOnlySet<(TextureAssetFamily Family, int ClassId)>? sourceAssetKeys = null,
        IReadOnlySet<TextureAssetFamily>? replacedFamilies = null)
    {
        var extracted = LevelAssetExtractor.ExtractLevelWad(GameId.UYA, sourceLevelWad.ToArray());
        if (extracted.FailedAssetCount > 0)
            throw new InvalidDataException(
                $"The base level has {extracted.FailedAssetCount} static assets that cannot be retained safely.");
        var source = extracted.Assets
            .Select(value => new StaticAssetInput(
                $"source:{value.Kind}:{value.ClassId}:{value.SourceIndex}",
                value.Kind switch
                {
                    FrontendAssetKind.Moby => TextureAssetFamily.Moby,
                    FrontendAssetKind.Tie => TextureAssetFamily.Tie,
                    FrontendAssetKind.Shrub => TextureAssetFamily.Shrub,
                    _ => throw new ArgumentOutOfRangeException(nameof(value.Kind)),
                },
                value.ClassId,
                value.DefinitionBytes,
                value.ModelBytes,
                value.Textures.Select(texture => new StaticAssetTexture(
                    texture.Role == 0 ? TextureRole.Material : TextureRole.Billboard,
                    texture.PifBytes ?? throw new InvalidDataException(
                        $"Base {value.Kind} class 0x{value.ClassId:X4} has an unreadable texture and cannot be retained safely.")))
                    .ToArray(),
                PreserveTextureIndexes: true))
            .ToDictionary(value => (value.Family, value.ClassId));
        selected = selected.Select(value =>
        {
            var key = (value.Family, value.ClassId);
            return sourceAssetKeys?.Contains(key) == true && source.TryGetValue(key, out var current)
                ? value with
                {
                    DefinitionBytes = current.DefinitionBytes,
                    ModelBytes = current.ModelBytes,
                    PreserveTextureIndexes = true,
                }
                : value;
        }).ToArray();
        var selectedKeys = selected.Select(value => (value.Family, value.ClassId)).ToHashSet();
        return source.Values.Where(value => !selectedKeys.Contains((value.Family, value.ClassId))
                && replacedFamilies?.Contains(value.Family) != true)
            .Concat(selected)
            .ToArray();
    }

    private static string SnapshotRoot(BakeStagingStore staging, BakeLayerId layer) =>
        ForgeProjectPersistence.ResolveRelativePath(
            staging.RootPath,
            staging.Manifest.Layers.Single(value => value.Layer == layer).RelativePath);

    private static T Read<T>(string root, string relativePath, string description) =>
        ForgeProjectPersistence.Deserialize<T>(
            File.ReadAllBytes(ForgeProjectPersistence.ResolveRelativePath(root, relativePath)), description);

    private static TextureAssetFamily TextureFamily(BakeLayerId layer) => layer switch
    {
        BakeLayerId.Mobys => TextureAssetFamily.Moby,
        BakeLayerId.Ties => TextureAssetFamily.Tie,
        BakeLayerId.Shrubs => TextureAssetFamily.Shrub,
        _ => throw new ArgumentOutOfRangeException(nameof(layer)),
    };
}
