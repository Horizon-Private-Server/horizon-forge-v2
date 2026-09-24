using Forge.Host.Domain;
using System.Security.Cryptography;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.IO;
using RatchetPs2.Core.LevelAssets;
using RatchetPs2.Core.Textures.Palettes;
using RatchetPs2.Core.Wad;
using RatchetPs2.Core.Wad.Models;
using RatchetPs2.Games.UYA.Level;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.UYA;

public static class UyaLevelPackService
{
    public static async Task<UyaLevelPackResult> PackAsync(
        string projectRoot,
        AssetCatalogStore catalog,
        ReadOnlyMemory<byte> sourceLevelWad,
        BakeFingerprintContext context,
        IReadOnlySet<string>? acknowledgedWarnings = null,
        IReadOnlySet<BakeLayerId>? deferredLayers = null,
        IProgress<LevelArchiveProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (sourceLevelWad.IsEmpty) throw new ArgumentException("Source level WAD cannot be empty.", nameof(sourceLevelWad));
        var validation = await UyaBakeValidationService.PreflightAsync(
            projectRoot, catalog, context, acknowledgedWarnings, cancellationToken: cancellationToken);
        var diagnostics = validation.Diagnostics.ToList();
        var staging = await BakeStagingStore.OpenAsync(projectRoot, cancellationToken);
        var stagedLayers = staging.Manifest.Layers.Select(value => value.Layer).ToHashSet();
        if (!validation.CanBake || validation.Plan.Layers.Any(value => value.State != BakeLayerState.Clean
            && (!(deferredLayers?.Contains(value.Layer) ?? false) || !stagedLayers.Contains(value.Layer))))
        {
            diagnostics.Add(Error(
                "UYA_PACK_STAGING_NOT_CURRENT",
                "Packing requires every selected layer current and every deferred layer to have staged output.",
                "Build the required layers and resolve all diagnostics before packing."));
            return Failed(diagnostics);
        }

        try
        {
            var sourcePackage = UyaLevelWadUnpacker.Unpack(sourceLevelWad.ToArray());
            var replacements = await CreateReplacementsAsync(
                catalog, staging, sourceLevelWad, sourcePackage,
                deferredLayers?.Any(UyaStaticLayerSchema.Layers.Contains) ?? false,
                cancellationToken);
            var archive = await Task.Run(
                () => LevelArchiveBuilder.Build(
                    GameId.UYA, sourceLevelWad, replacements, progress: progress,
                    cancellationToken: cancellationToken),
                cancellationToken);
            diagnostics.AddRange(archive.Warnings.Select((value, index) => new BakeDiagnostic(
                $"UYA_ARCHIVE_WARNING_{index + 1:D2}",
                BakeDiagnosticSeverity.Warning,
                null,
                null,
                null,
                value,
                "Review the SDK warning before patching.")));
            diagnostics.AddRange(archive.Diagnostics.Select(value => new BakeDiagnostic(
                value.Code,
                value.Blocking ? BakeDiagnosticSeverity.Error : BakeDiagnosticSeverity.Warning,
                null,
                null,
                null,
                value.Message,
                "Correct the staged layer or source WAD and retry packing.")));
            if (!archive.Succeeded || archive.OutputBytes is null) return Failed(diagnostics, archive);
            ValidateOutput(archive.OutputBytes, replacements, staging, sourcePackage);
            return new(
                true,
                archive.OutputBytes,
                archive.CompressedSha256,
                archive.ChangedRegions,
                archive.Compressions,
                diagnostics);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException
            or UnauthorizedAccessException or ArgumentException or OverflowException)
        {
            diagnostics.Add(Error(
                "UYA_PACK_STAGING_INVALID",
                exception.Message,
                "Re-bake from the verified source level and retry packing."));
            return Failed(diagnostics);
        }
    }

    private static async Task<Dictionary<string, ReadOnlyMemory<byte>>> CreateReplacementsAsync(
        AssetCatalogStore catalog,
        BakeStagingStore staging,
        ReadOnlyMemory<byte> sourceLevelWad,
        UyaLevelWadPackage sourcePackage,
        bool allowStalePaletteReport,
        CancellationToken cancellationToken)
    {
        var replacements = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        var sourceBase = UyaBaseLayerService.Extract(sourcePackage)
            .ToDictionary(value => (value.Layer, value.Name));
        ReadOnlyMemory<byte>? terrain = null;
        ReadOnlyMemory<byte>? sky = null;
        ReadOnlyMemory<byte>? collision = null;
        foreach (var layer in UyaBaseLayerSchema.Layers)
        {
            var root = SnapshotRoot(staging, layer);
            var manifest = Read<UyaBaseLayerManifest>(root, "manifest.json", "staged base layer");
            foreach (var asset in manifest.Layers.Single(value => value.Layer == layer).Assets)
            {
                var bytes = await File.ReadAllBytesAsync(Path.Combine(root, asset.Name), cancellationToken);
                if (!sourceBase.TryGetValue((layer, asset.Name), out var source))
                    throw new InvalidDataException($"{layer} asset {asset.Name} is absent from the base level.");
                var path = GameplayPath(layer, asset.Name);
                if (path is not null)
                {
                    replacements.Add(path, bytes);
                    continue;
                }
                if (bytes.AsSpan().SequenceEqual(source.Bytes)) continue;
                switch (layer, asset.Name)
                {
                    case (BakeLayerId.Sky, "sky.bin"):
                        sky = bytes;
                        break;
                    case (BakeLayerId.Tfrags, "primary.bin"):
                        terrain = bytes;
                        break;
                    case (BakeLayerId.Collision, "collision.bin"):
                        collision = bytes;
                        break;
                    case (BakeLayerId.Tfrags, var name) when TryChunkIndex(name, out var chunkIndex):
                        var chunkPath = $"level_wad/chunks/chunk{chunkIndex}.wad";
                        var sourceChunk = sourcePackage.Files.Single(value => value.Path == chunkPath);
                        replacements.Add(chunkPath,
                            LevelAssetComposer.ComposeTfragChunk(
                                GameId.UYA, sourceChunk.Bytes, bytes, cancellationToken: cancellationToken));
                        break;
                    default:
                        throw new InvalidDataException($"{layer} asset {asset.Name} has no SDK packing route.");
                }
            }
        }

        var assets = UyaLevelWadRenderPackageBuilder.ReadAssetSourceFiles(sourcePackage.Files);
        var assetWad = BinaryMagic.IsWad(assets.AssetWadBytes)
            ? WadCompression.Decompress(assets.AssetWadBytes, new(), cancellationToken)
            : assets.AssetWadBytes;
        var composed = terrain is not null || sky is not null || collision is not null
            ? LevelAssetComposer.ComposeAssetWad(
                GameId.UYA, assets.HeaderBytes, assetWad, new(terrain, sky, collision), cancellationToken)
            : new LevelAssetWadComposition(assets.HeaderBytes, assetWad);

        var staticAssets = new List<StaticAssetInput>();
        var sourceLevel = $"level{sourcePackage.LevelWad.Level:00}";
        var staticAssetsChanged = false;
        foreach (var layer in UyaStaticLayerSchema.Layers)
        {
            var root = SnapshotRoot(staging, layer);
            var manifest = Read<UyaStaticBakeManifest>(root, "manifest.json", $"staged {layer} layer");
            foreach (var definition in manifest.Definitions)
            {
                var entry = catalog.Query(new(Id: definition.Asset.Id)).SingleOrDefault();
                if (entry is null || entry.CanonicalFormatVersion != UyaAssetImportService.CanonicalFormatVersion)
                    throw new InvalidDataException($"{layer} definition {definition.ClassId:X4} has no current canonical asset.");
                staticAssetsChanged |= !entry.Sources.Any(value =>
                    value.Game == "UYA" && value.Region == "NTSC-U" && value.Level == sourceLevel);
                var path = ForgeProjectPersistence.ResolveRelativePath(root, definition.Resource);
                var canonical = UyaCanonicalAssetCodec.Decode(await File.ReadAllBytesAsync(path, cancellationToken));
                staticAssets.Add(new(
                    definition.Asset.Id.ToString(),
                    TextureFamily(layer),
                    definition.ClassId,
                    canonical.DefinitionBytes,
                    canonical.ModelBytes,
                    canonical.Textures.Select(value => new StaticAssetTexture(
                        value.Role == 0 ? TextureRole.Material : TextureRole.Billboard,
                        value.PifBytes)).ToArray()));
            }
            replacements.Add($"gameplay/core/{layer.ToString().ToLowerInvariant().TrimEnd('s')}_instances.bin",
                await File.ReadAllBytesAsync(Path.Combine(root, "instances.bin"), cancellationToken));
        }
        var staticComposition = StaticAssetComposer.Compose(
            GameId.UYA,
            composed.HeaderBytes,
            composed.AssetWadBytes,
            assets.PaletteBytes,
            staticAssets,
            cancellationToken);
        var actualPaletteReport = PaletteBakeReportService.Create(
            staticComposition.Inventory, staticComposition.Optimization);
        var stagedPaletteReport = staging.Manifest.PaletteReport
            ?? throw new InvalidDataException("Staged output is missing its palette report.");
        if (allowStalePaletteReport)
            await staging.SetPaletteReportAsync(actualPaletteReport, cancellationToken);
        else if (!PaletteBakeReportService.Equivalent(stagedPaletteReport, actualPaletteReport))
            throw new InvalidDataException("Staged palette report does not match the generated asset payloads.");
        if (staticAssetsChanged)
        {
            replacements.Add("assets/asset_header.bin", staticComposition.HeaderBytes);
            replacements.Add("assets/palette.bin", staticComposition.PaletteBytes);
            replacements.Add("assets/asset_wad_payload.bin", staticComposition.AssetWadBytes);
        }
        else if (terrain is not null || sky is not null || collision is not null)
        {
            replacements.Add("assets/asset_header.bin", composed.HeaderBytes);
            replacements.Add("assets/asset_wad_payload.bin", composed.AssetWadBytes);
        }

        var gameplayRoot = SnapshotRoot(staging, BakeLayerId.Gameplay);
        var gameplay = Read<UyaGameplayBakeManifest>(gameplayRoot, "manifest.json", "staged gameplay layer");
        foreach (var section in gameplay.Sections)
            replacements.Add($"gameplay/core/{section.Name}.bin",
                await File.ReadAllBytesAsync(
                    ForgeProjectPersistence.ResolveRelativePath(gameplayRoot, section.Path), cancellationToken));

        ValidateOpaque(SnapshotRoot(staging, BakeLayerId.Opaque), sourceLevelWad, sourcePackage);
        return replacements;
    }

    private static void ValidateOutput(
        byte[] output,
        IReadOnlyDictionary<string, ReadOnlyMemory<byte>> replacements,
        BakeStagingStore staging,
        UyaLevelWadPackage sourcePackage)
    {
        var inventory = UyaLevelWadInventoryReader.Read(output);
        foreach (var replacement in replacements)
        {
            var slot = inventory.Containers.SelectMany(value => value.Slots)
                .Single(value => value.LogicalPaths.Contains(replacement.Key, StringComparer.Ordinal));
            if (!EquivalentPayload(replacement.Value.Span, slot.Bytes.Span))
                throw new InvalidDataException($"Packed output does not contain staged payload {replacement.Key}.");
        }
        var outputPackage = UyaLevelWadUnpacker.Unpack(output);
        ValidateOpaque(SnapshotRoot(staging, BakeLayerId.Opaque), output, outputPackage);

        var expectedBase = UyaBaseLayerSchema.Layers.SelectMany(layer =>
        {
            var root = SnapshotRoot(staging, layer);
            var manifest = Read<UyaBaseLayerManifest>(root, "manifest.json", "staged base layer");
            return manifest.Layers.Single(value => value.Layer == layer).Assets
                .Where(value => GameplayPath(layer, value.Name) is null)
                .Select(value => ((layer, value.Name), File.ReadAllBytes(Path.Combine(root, value.Name))));
        }).ToDictionary(value => value.Item1, value => value.Item2);
        var actualBase = UyaBaseLayerService.Extract(outputPackage)
            .Where(value => GameplayPath(value.Layer, value.Name) is null)
            .ToDictionary(value => (value.Layer, value.Name), value => value.Bytes);
        if (expectedBase.Count != actualBase.Count
            || expectedBase.Any(value => !actualBase.TryGetValue(value.Key, out var bytes)
                || !EquivalentPayload(value.Value, bytes)))
            throw new InvalidDataException("Packed output does not semantically match the staged base-layer payloads.");
    }

    private static void ValidateOpaque(
        string stagedRoot,
        ReadOnlyMemory<byte> levelWad,
        UyaLevelWadPackage package)
    {
        var manifest = Read<OpaqueContentManifest>(stagedRoot, OpaqueContentStore.ManifestFileName, "staged opaque layer");
        var source = UyaOpaqueContentService.Capture(levelWad.Span, package)
            .ToDictionary(value => value.Name, StringComparer.Ordinal);
        if (manifest.Sections.Count != source.Count)
            throw new InvalidDataException("Opaque staged section inventory does not match the source level.");
        foreach (var section in manifest.Sections)
        {
            if (!source.TryGetValue(section.Name, out var captured)
                || captured.Bytes.LongLength != section.Size
                || Convert.ToHexString(SHA256.HashData(captured.Bytes)).ToLowerInvariant() != section.Checksum)
                throw new InvalidDataException($"Opaque section {section.Name} does not match the staged source hash.");
        }
    }

    private static string SnapshotRoot(BakeStagingStore staging, BakeLayerId layer) =>
        ForgeProjectPersistence.ResolveRelativePath(
            staging.RootPath,
            staging.Manifest.Layers.Single(value => value.Layer == layer).RelativePath);

    private static T Read<T>(string root, string relativePath, string description) =>
        ForgeProjectPersistence.Deserialize<T>(
            File.ReadAllBytes(ForgeProjectPersistence.ResolveRelativePath(root, relativePath)), description);

    private static string? GameplayPath(BakeLayerId layer, string name) => (layer, name) switch
    {
        (BakeLayerId.World, "level-settings.bin") => "gameplay/core/level_settings.bin",
        (BakeLayerId.Lighting, "directional-lights.bin") => "gameplay/core/directional_lights.bin",
        (BakeLayerId.Lighting, "point-lights.bin") => "gameplay/core/point_lights.bin",
        (BakeLayerId.Lighting, "tie-ambient-rgbas.bin") => "gameplay/core/tie_ambient_rgbas.bin",
        _ => null,
    };

    private static bool TryChunkIndex(string name, out int index)
    {
        index = 0;
        return name.StartsWith("chunk-", StringComparison.Ordinal)
            && name.EndsWith(".bin", StringComparison.Ordinal)
            && int.TryParse(name.AsSpan(6, name.Length - 10), out index)
            && index > 0;
    }

    private static TextureAssetFamily TextureFamily(BakeLayerId layer) => layer switch
    {
        BakeLayerId.Mobys => TextureAssetFamily.Moby,
        BakeLayerId.Ties => TextureAssetFamily.Tie,
        BakeLayerId.Shrubs => TextureAssetFamily.Shrub,
        _ => throw new ArgumentOutOfRangeException(nameof(layer)),
    };

    private static bool EquivalentPayload(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual) =>
        actual.Length >= expected.Length
        && actual[..expected.Length].SequenceEqual(expected)
        && actual[expected.Length..].ContainsAnyExcept((byte)0) == false;

    private static BakeDiagnostic Error(string code, string cause, string action) =>
        new(code, BakeDiagnosticSeverity.Error, null, null, null, cause, action);

    private static UyaLevelPackResult Failed(
        IReadOnlyList<BakeDiagnostic> diagnostics,
        LevelArchiveBuildResult? archive = null) =>
        new(false, null, null, archive?.ChangedRegions ?? [], archive?.Compressions ?? [], diagnostics);
}
