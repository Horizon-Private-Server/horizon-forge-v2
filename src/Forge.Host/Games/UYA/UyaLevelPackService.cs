using Forge.Host.Domain;
using System.Buffers.Binary;
using System.Security.Cryptography;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.IO;
using RatchetPs2.Core.LevelAssets;
using RatchetPs2.Core.Textures.Palettes;
using RatchetPs2.Core.Wad;
using RatchetPs2.Core.Wad.Models;
using RatchetPs2.Games.UYA.Gameplay;
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
            var workspace = await ForgeProjectWorkspace.OpenAsync(projectRoot, cancellationToken);
            var allowStalePaletteReport = deferredLayers?.Any(UyaStaticLayerSchema.Layers.Contains) == true
                || File.Exists(Path.Combine(
                    SnapshotRoot(staging, BakeLayerId.Ties), "visibility-bit.bin"));
            if (!allowStalePaletteReport)
            {
                var expectedPaletteReport = PaletteBakeReportService.Create(
                    await UyaTextureInventoryService.BuildAsync(projectRoot, catalog, cancellationToken),
                    cancellationToken);
                if (staging.Manifest.PaletteReport is null
                    || !PaletteBakeReportService.Equivalent(staging.Manifest.PaletteReport, expectedPaletteReport))
                    throw new InvalidDataException("Staged palette report does not match the staged static assets.");
            }
            var replacements = await CreateReplacementsAsync(
                catalog, staging, sourceLevelWad, sourcePackage, workspace.Manifest.BaseLevel,
                allowStalePaletteReport,
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
            await ValidateOutputAsync(archive.OutputBytes, replacements, staging, sourcePackage, cancellationToken);
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
        ProjectBaseLevel baseLevel,
        bool allowStalePaletteReport,
        CancellationToken cancellationToken)
    {
        var replacements = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        var sourceBase = UyaBaseLayerService.Extract(sourcePackage)
            .ToDictionary(value => (value.Layer, value.Name));
        ReadOnlyMemory<byte>? terrain = null;
        ReadOnlyMemory<byte>? sky = null;
        ReadOnlyMemory<byte>? collision = null;
        var chunkReplacements = new Dictionary<int, byte[]>();
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
                        var currentChunk = chunkReplacements.GetValueOrDefault(chunkIndex) ?? sourceChunk.Bytes;
                        chunkReplacements[chunkIndex] = LevelAssetComposer.ComposeTfragChunk(
                            GameId.UYA, currentChunk, bytes, cancellationToken: cancellationToken);
                        break;
                    case (BakeLayerId.Collision, var name) when TryChunkIndex(name, out var chunkIndex):
                        var collisionChunkPath = $"level_wad/chunks/chunk{chunkIndex}.wad";
                        var sourceCollisionChunk = sourcePackage.Files.Single(value => value.Path == collisionChunkPath);
                        var currentCollisionChunk = chunkReplacements.GetValueOrDefault(chunkIndex) ?? sourceCollisionChunk.Bytes;
                        chunkReplacements[chunkIndex] = LevelAssetComposer.ComposeTfragChunkCollision(
                            GameId.UYA, currentCollisionChunk, bytes, cancellationToken: cancellationToken);
                        break;
                    default:
                        throw new InvalidDataException($"{layer} asset {asset.Name} has no SDK packing route.");
                }
            }
        }
        foreach (var (chunkIndex, bytes) in chunkReplacements)
            replacements.Add($"level_wad/chunks/chunk{chunkIndex}.wad", bytes);

        var assets = UyaLevelWadRenderPackageBuilder.ReadAssetSourceFiles(sourcePackage.Files);
        var assetWad = BinaryMagic.IsWad(assets.AssetWadBytes)
            ? WadCompression.Decompress(assets.AssetWadBytes, new(), cancellationToken)
            : assets.AssetWadBytes;
        var tieRoot = SnapshotRoot(staging, BakeLayerId.Ties);
        var visibilityBitPath = Path.Combine(tieRoot, "visibility-bit.bin");
        int? visibilityBit = File.Exists(visibilityBitPath)
            ? BinaryPrimitives.ReadInt32LittleEndian(await File.ReadAllBytesAsync(visibilityBitPath, cancellationToken))
            : null;
        var composed = visibilityBit is { } bitIndex
            ? LevelAssetComposer.SetAlwaysVisibleOcclusionBit(
                GameId.UYA, assets.HeaderBytes, assetWad, bitIndex, cancellationToken)
            : new LevelAssetWadComposition(assets.HeaderBytes, assetWad);
        if (terrain is not null || sky is not null || collision is not null)
            composed = LevelAssetComposer.ComposeAssetWad(
                GameId.UYA, composed.HeaderBytes, composed.AssetWadBytes,
                new(terrain, sky, collision), cancellationToken);

        var staticAssets = new List<StaticAssetInput>();
        var sourceStaticAssetKeys = new HashSet<(TextureAssetFamily Family, int ClassId)>();
        var sourceLevel = $"level{baseLevel.Level:00}";
        var staticAssetsChanged = false;
        foreach (var layer in UyaStaticLayerSchema.Layers)
        {
            var root = SnapshotRoot(staging, layer);
            var manifest = Read<UyaStaticBakeManifest>(root, "manifest.json", $"staged {layer} layer");
            var familyName = layer.ToString().ToLowerInvariant().TrimEnd('s');
            foreach (var definition in manifest.Definitions)
            {
                var entry = catalog.Query(new(Id: definition.Asset.Id)).SingleOrDefault();
                if (entry is null || entry.CanonicalFormatVersion != UyaAssetImportService.CanonicalFormatVersion)
                    throw new InvalidDataException($"{layer} definition {definition.ClassId:X4} has no current canonical asset.");
                var fromSourceLevel = entry.Sources.Any(value =>
                    value.Game == baseLevel.Game
                    && value.Region == baseLevel.Region
                    && value.Revision == baseLevel.Revision
                    && value.Level == sourceLevel);
                staticAssetsChanged |= !fromSourceLevel;
                var path = ForgeProjectPersistence.ResolveRelativePath(root, definition.Resource);
                var canonical = UyaCanonicalAssetCodec.Decode(await File.ReadAllBytesAsync(path, cancellationToken));
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
                if (fromSourceLevel)
                    sourceStaticAssetKeys.Add((input.Family, input.ClassId));
            }
            replacements.Add($"gameplay/core/{familyName}_classes.bin",
                UyaClassIdListWriter.Write(manifest.Instances.Select(value => value.ClassId)));
            replacements.Add($"gameplay/core/{familyName}_instances.bin",
                await File.ReadAllBytesAsync(Path.Combine(root, "instances.bin"), cancellationToken));
            if (layer is BakeLayerId.Ties or BakeLayerId.Shrubs)
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
        if (staticAssetsChanged || visibilityBit is not null)
            staticAssets = IncludeSourceStaticAssets(sourceLevelWad.Span, staticAssets, sourceStaticAssetKeys)
                .ToList();
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
        if (allowStalePaletteReport || staticAssetsChanged)
            await staging.SetPaletteReportAsync(actualPaletteReport, cancellationToken);
        else if (!PaletteBakeReportService.Equivalent(stagedPaletteReport, actualPaletteReport))
            throw new InvalidDataException("Staged palette report does not match the generated asset payloads.");
        if (staticAssetsChanged || visibilityBit is not null)
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

        var fxHeader = replacements.TryGetValue("assets/asset_header.bin", out var replacedHeader)
            ? replacedHeader.ToArray()
            : assets.HeaderBytes;
        var fxAsset = replacements.TryGetValue("assets/asset_wad_payload.bin", out var replacedAsset)
            ? replacedAsset.ToArray()
            : assetWad;
        var fxComposition = await UyaFxBakeService.ComposeAsync(
            SnapshotRoot(staging, BakeLayerId.Fx), fxHeader, fxAsset, cancellationToken);
        if (fxComposition is not null)
        {
            replacements["assets/asset_header.bin"] = fxComposition.HeaderBytes;
            replacements["assets/asset_wad_payload.bin"] = fxComposition.AssetBytes;
        }

        var gameplayRoot = SnapshotRoot(staging, BakeLayerId.Gameplay);
        var gameplay = Read<UyaGameplayBakeManifest>(gameplayRoot, "manifest.json", "staged gameplay layer");
        foreach (var section in gameplay.Sections)
            replacements.Add($"gameplay/core/{section.Name}.bin",
                await File.ReadAllBytesAsync(
                    ForgeProjectPersistence.ResolveRelativePath(gameplayRoot, section.Path), cancellationToken));

        await UyaHudBakeService.AddReplacementsAsync(
            SnapshotRoot(staging, BakeLayerId.Hud), replacements, cancellationToken);

        ValidateOpaque(SnapshotRoot(staging, BakeLayerId.Opaque), sourceLevelWad, sourcePackage,
            ReplacedOpaqueSections(replacements));
        return replacements;
    }

    internal static IReadOnlyList<StaticAssetInput> IncludeSourceStaticAssets(
        ReadOnlySpan<byte> sourceLevelWad,
        IReadOnlyList<StaticAssetInput> selected,
        IReadOnlySet<(TextureAssetFamily Family, int ClassId)>? sourceAssetKeys = null)
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
        return source.Values.Where(value => !selectedKeys.Contains((value.Family, value.ClassId)))
            .Concat(selected)
            .ToArray();
    }

    private static async Task ValidateOutputAsync(
        byte[] output,
        IReadOnlyDictionary<string, ReadOnlyMemory<byte>> replacements,
        BakeStagingStore staging,
        UyaLevelWadPackage sourcePackage,
        CancellationToken cancellationToken)
    {
        var inventory = UyaLevelWadInventoryReader.Read(output);
        foreach (var replacement in replacements)
        {
            var slot = inventory.Containers.SelectMany(value => value.Slots)
                .Single(value => value.LogicalPaths.Contains(replacement.Key, StringComparer.Ordinal));
            var actual = replacement.Key.StartsWith("hud/bank", StringComparison.Ordinal)
                && BinaryMagic.IsWad(slot.Bytes.Span)
                ? WadCompression.Decompress(slot.Bytes.Span)
                : slot.Bytes.ToArray();
            var equivalent = replacement.Key == "assets/asset_header.bin"
                ? UyaLevelWadValidator.EquivalentAssetHeader(replacement.Value.Span, actual)
                : EquivalentPayload(replacement.Value.Span, actual);
            if (!equivalent)
                throw new InvalidDataException($"Packed output does not contain staged payload {replacement.Key}.");
        }
        var outputPackage = UyaLevelWadUnpacker.Unpack(output);
        UyaLevelWadValidator.Validate(inventory, outputPackage);
        UyaHudBakeService.ValidatePacked(SnapshotRoot(staging, BakeLayerId.Hud), outputPackage);
        await UyaFxBakeService.ValidatePackedAsync(
            SnapshotRoot(staging, BakeLayerId.Fx), outputPackage, cancellationToken);
        ValidateOpaque(SnapshotRoot(staging, BakeLayerId.Opaque), output, outputPackage,
            ReplacedOpaqueSections(replacements), allowAlignmentPadding: true);

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
        UyaLevelWadPackage package,
        IReadOnlySet<string> replacedSections,
        bool allowAlignmentPadding = false)
    {
        var manifest = Read<OpaqueContentManifest>(stagedRoot, OpaqueContentStore.ManifestFileName, "staged opaque layer");
        var source = UyaOpaqueContentService.Capture(levelWad.Span, package)
            .Where(value => !replacedSections.Contains(value.Name))
            .ToDictionary(value => value.Name, StringComparer.Ordinal);
        var sections = manifest.Sections.Where(value => !replacedSections.Contains(value.Name)).ToArray();
        if (sections.Length != source.Count)
            throw new InvalidDataException("Opaque staged section inventory does not match the source level.");
        foreach (var section in sections)
        {
            if (!source.TryGetValue(section.Name, out var captured))
                throw new InvalidDataException($"Opaque section {section.Name} does not match the staged source hash.");
            if (captured.Bytes.LongLength == section.Size
                && Convert.ToHexString(SHA256.HashData(captured.Bytes)).ToLowerInvariant() == section.Checksum)
                continue;
            var staged = File.ReadAllBytes(ForgeProjectPersistence.ResolveRelativePath(stagedRoot, section.Blob));
            if (!allowAlignmentPadding || !EquivalentWithTrailingZeroPadding(staged, captured.Bytes))
                throw new InvalidDataException($"Opaque section {section.Name} does not match the staged source hash.");
        }
    }

    private static bool EquivalentWithTrailingZeroPadding(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        var common = Math.Min(left.Length, right.Length);
        return left[..common].SequenceEqual(right[..common])
            && (left.Length > common ? left[common..] : right[common..]).ContainsAnyExcept((byte)0) == false;
    }

    private static HashSet<string> ReplacedOpaqueSections(
        IReadOnlyDictionary<string, ReadOnlyMemory<byte>> replacements)
    {
        var sections = replacements.Keys
            .Where(value => value.StartsWith("gameplay/core/", StringComparison.Ordinal)
                && value.EndsWith(".bin", StringComparison.Ordinal))
            .Select(value => $"gameplay/{Path.GetFileNameWithoutExtension(value)}")
            .ToHashSet(StringComparer.Ordinal);
        if (replacements.ContainsKey("hud/header.bin")) sections.Add("level-data/hud-header");
        for (var index = 0; index < ProjectHudSchema.PhysicalBankCount; index++)
            if (replacements.ContainsKey($"hud/bank{index}.bin"))
                sections.Add($"level-data/hud-bank-{index}");
        return sections;
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
