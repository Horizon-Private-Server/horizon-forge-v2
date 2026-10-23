using Forge.Host.Domain;
using System.Buffers.Binary;
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
            var replacementPlan = await CreateReplacementsAsync(
                catalog, staging, sourceLevelWad, sourcePackage, workspace,
                allowStalePaletteReport,
                cancellationToken);
            var archive = await Task.Run(
                () => LevelArchiveBuilder.Build(
                    GameId.UYA, sourceLevelWad, replacementPlan.Replacements, progress: progress,
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
            await ValidateOutputAsync(
                archive.OutputBytes,
                replacementPlan.Replacements,
                replacementPlan.ExpectedPrimaryTerrain,
                staging,
                sourcePackage,
                cancellationToken);
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
            or UnauthorizedAccessException or ArgumentException or OverflowException
            or NotSupportedException)
        {
            diagnostics.Add(Error(
                "UYA_PACK_STAGING_INVALID",
                exception.Message,
                "Re-bake from the verified source level and retry packing."));
            return Failed(diagnostics);
        }
    }

    private static async Task<ReplacementPlan> CreateReplacementsAsync(
        AssetCatalogStore catalog,
        BakeStagingStore staging,
        ReadOnlyMemory<byte> sourceLevelWad,
        UyaLevelWadPackage sourcePackage,
        ForgeProjectWorkspace workspace,
        bool allowStalePaletteReport,
        CancellationToken cancellationToken)
    {
        var baseLevel = workspace.Manifest.BaseLevel;
        var replacements = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        var importedOcclusion = await UyaOcclusionImportPayload.ReadAsync(workspace, cancellationToken);
        if (importedOcclusion is not null)
            replacements.Add("occlusion/occlusion.bin", importedOcclusion.LevelOcclusion);
        var sourceBase = UyaBaseLayerService.Extract(sourcePackage)
            .ToDictionary(value => (value.Layer, value.Name));
        ReadOnlyMemory<byte>? terrain = null;
        ReadOnlyMemory<byte>? sky = null;
        ReadOnlyMemory<byte>? collision = null;
        var maximumTerrainLength = 0;
        var maximumCollisionLength = 0;
        var chunkReplacements = new Dictionary<int, byte[]>();
        var chunkTerrains = new Dictionary<int, byte[]>();
        foreach (var layer in UyaBaseLayerSchema.Layers)
        {
            var root = SnapshotRoot(staging, layer);
            var manifest = Read<UyaBaseLayerManifest>(root, "manifest.json", "staged base layer");
            foreach (var asset in manifest.Layers.Single(value => value.Layer == layer).Assets)
            {
                var bytes = await File.ReadAllBytesAsync(Path.Combine(root, asset.Name), cancellationToken);
                if (!sourceBase.TryGetValue((layer, asset.Name), out var source))
                {
                    if (layer == BakeLayerId.Sky && asset.Name == "sky.bin")
                    {
                        sky = bytes;
                        continue;
                    }
                    throw new InvalidDataException($"{layer} asset {asset.Name} is absent from the base level.");
                }
                if (layer == BakeLayerId.Tfrags)
                    maximumTerrainLength = Math.Max(maximumTerrainLength, bytes.Length);
                else if (layer == BakeLayerId.Collision)
                    maximumCollisionLength = Math.Max(maximumCollisionLength, bytes.Length);
                var path = GameplayPath(layer, asset.Name);
                if (path is not null)
                {
                    replacements.Add(path, bytes);
                    continue;
                }
                if (layer == BakeLayerId.Tfrags && bytes.Length == 0) continue;
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
                        chunkTerrains[chunkIndex] = bytes;
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
        // UYA streams every chunk through the primary terrain and collision buffers.
        terrain = ExpandPrimaryStreamBuffer(
            terrain, sourceBase[(BakeLayerId.Tfrags, "primary.bin")].Bytes, maximumTerrainLength);
        collision = ExpandPrimaryStreamBuffer(
            collision, sourceBase[(BakeLayerId.Collision, "collision.bin")].Bytes, maximumCollisionLength);
        var assets = UyaLevelWadRenderPackageBuilder.ReadAssetSourceFiles(sourcePackage.Files);
        UyaSoundCompositionResult? soundComposition = null;
        if (workspace.Content.Sounds.Programs.Count > 0)
        {
            var sourceSoundBank = sourcePackage.Files.SingleOrDefault(
                    value => value.Path == "level_wad/sound.bnk")?.Bytes
                ?? throw new InvalidDataException("The source level has no retained sound bank.");
            soundComposition = await UyaSoundCompositionService.ComposeAsync(
                workspace, sourceSoundBank, assets.HeaderBytes, cancellationToken);
        }
        if (soundComposition?.BankChanged == true)
            replacements.Add("level_wad/sound.bnk", soundComposition.BankBytes);
        var stagedTerrains = chunkTerrains.ToDictionary(
            value => (ProjectTfragSourceKind.Chunk, value.Key), value => value.Value);
        if (terrain is not null)
            stagedTerrains.Add((ProjectTfragSourceKind.Primary, 0), terrain.Value.ToArray());
        var customTextures = await UyaCustomTfragTextureService.ComposeAsync(
            workspace, stagedTerrains, cancellationToken);
        var customTfragTextures = customTextures.Textures;
        var tieRoot = SnapshotRoot(staging, BakeLayerId.Ties);
        var visibilityBitPath = Path.Combine(tieRoot, "visibility-bit.bin");
        int? visibilityBit = File.Exists(visibilityBitPath)
            ? BinaryPrimitives.ReadInt32LittleEndian(await File.ReadAllBytesAsync(visibilityBitPath, cancellationToken))
            : null;
        var assetWad = BinaryMagic.IsWad(assets.AssetWadBytes)
            ? WadCompression.Decompress(assets.AssetWadBytes, new(), cancellationToken)
            : assets.AssetWadBytes;
        var composed = new LevelAssetWadComposition(assets.HeaderBytes, assetWad);
        if (terrain is { IsEmpty: true }) terrain = null;
        var appendSkyAfterStaticComposition = sky is not null
            && !sourceBase.ContainsKey((BakeLayerId.Sky, "sky.bin"));
        var skyBeforeStaticComposition = appendSkyAfterStaticComposition ? null : sky;
        ReadOnlyMemory<byte>? assetOcclusion = null;
        if (importedOcclusion is not null)
            assetOcclusion = importedOcclusion.AssetGrid;
        if (terrain is not null || skyBeforeStaticComposition is not null || collision is not null
            || importedOcclusion is not null)
            composed = LevelAssetComposer.ComposeAssetWad(
                GameId.UYA, composed.HeaderBytes, composed.AssetWadBytes,
                new(terrain, skyBeforeStaticComposition, collision, assetOcclusion),
                cancellationToken);

        var staticPack = await UyaStaticAssetPackService.ComposeAsync(
            workspace,
            catalog,
            staging,
            sourceLevelWad,
            sourcePackage,
            baseLevel,
            assets,
            composed,
            customTfragTextures,
            replacements,
            allowStalePaletteReport,
            cancellationToken);
        foreach (var (chunkIndex, chunkTerrain) in chunkTerrains)
        {
            var key = (ProjectTfragSourceKind.Chunk, chunkIndex);
            var remaps = customTextures.TextureAssetIdsByOwner.GetValueOrDefault(key)
                ?? new Dictionary<int, IReadOnlyDictionary<int, string>>();
            var finalized = TfragTerrainComposer.FinalizeTerrain(
                GameId.UYA, chunkTerrain, remaps, staticPack.Composition.TerrainTextures,
                firstOcclusionMappingIndex: null, cancellationToken);
            var chunkPath = $"level_wad/chunks/chunk{chunkIndex}.wad";
            var sourceChunk = sourcePackage.Files.Single(value => value.Path == chunkPath);
            var currentChunk = chunkReplacements.GetValueOrDefault(chunkIndex) ?? sourceChunk.Bytes;
            chunkReplacements[chunkIndex] = LevelAssetComposer.ComposeTfragChunk(
                GameId.UYA, currentChunk, finalized, cancellationToken: cancellationToken);
        }
        foreach (var (chunkIndex, bytes) in chunkReplacements)
            replacements.Add($"level_wad/chunks/chunk{chunkIndex}.wad", bytes);
        byte[]? tfragOcclusion = null;
        if (terrain is not null)
        {
            var tfragPack = await UyaTfragPackService.ComposeAsync(
                workspace,
                sourcePackage,
                tieRoot,
                terrain.Value.ToArray(),
                sourceBase[(BakeLayerId.Tfrags, "primary.bin")].Bytes,
                customTextures.TextureAssetIdsByOwner.GetValueOrDefault(
                    (ProjectTfragSourceKind.Primary, 0))
                    ?? new Dictionary<int, IReadOnlyDictionary<int, string>>(),
                staticPack.Composition.TerrainTextures,
                visibilityBit,
                cancellationToken);
            terrain = tfragPack.TerrainBytes;
            tfragOcclusion = tfragPack.OcclusionBytes;
            visibilityBit = tfragPack.VisibilityBit;
        }
        if (tfragOcclusion is not null)
            replacements["gameplay/core/occlusion.bin"] = tfragOcclusion;
        var staticComposition = staticPack.Composition;
        var staticAssetsChanged = staticPack.AssetsChanged;
        var finalAssets = terrain is null && !appendSkyAfterStaticComposition
            ? staticPack.FinalAssets
            : LevelAssetComposer.ComposeAssetWad(
                GameId.UYA,
                staticPack.FinalAssets.HeaderBytes,
                staticPack.FinalAssets.AssetWadBytes,
                new(Terrain: terrain, Sky: appendSkyAfterStaticComposition ? sky : null),
                cancellationToken);
        if (visibilityBit is { } bitIndex)
            finalAssets = LevelAssetComposer.SetAlwaysVisibleOcclusionBit(
                GameId.UYA, finalAssets.HeaderBytes, finalAssets.AssetWadBytes, bitIndex, cancellationToken);
        if (staticAssetsChanged || visibilityBit is not null)
        {
            replacements.Add("assets/asset_header.bin", finalAssets.HeaderBytes);
            replacements.Add("assets/palette.bin", staticComposition.PaletteBytes);
            replacements.Add("assets/asset_wad_payload.bin", finalAssets.AssetWadBytes);
        }
        else if (terrain is not null || sky is not null || collision is not null
            || importedOcclusion is not null)
        {
            replacements.Add("assets/asset_header.bin", finalAssets.HeaderBytes);
            replacements.Add("assets/asset_wad_payload.bin", finalAssets.AssetWadBytes);
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
        if (soundComposition?.RemapChanged == true)
        {
            var currentHeader = replacements.TryGetValue("assets/asset_header.bin", out var soundHeader)
                ? soundHeader.Span
                : assets.HeaderBytes;
            replacements["assets/asset_header.bin"] = SoundBankComposer.ComposeRemap(
                GameId.UYA, currentHeader, soundComposition.Remap, cancellationToken);
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
        return new(replacements, terrain?.ToArray());
    }

    internal static IReadOnlyList<StaticAssetInput> IncludeSourceStaticAssets(
        ReadOnlySpan<byte> sourceLevelWad,
        IReadOnlyList<StaticAssetInput> selected,
        IReadOnlySet<(TextureAssetFamily Family, int ClassId)>? sourceAssetKeys = null) =>
        UyaStaticAssetPackService.IncludeSourceStaticAssets(
            sourceLevelWad, selected, sourceAssetKeys);

    private static async Task ValidateOutputAsync(
        byte[] output,
        IReadOnlyDictionary<string, ReadOnlyMemory<byte>> replacements,
        byte[]? expectedPrimaryTerrain,
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
        if (expectedPrimaryTerrain is not null)
            expectedBase[(BakeLayerId.Tfrags, "primary.bin")] = expectedPrimaryTerrain;
        var actualBase = UyaBaseLayerService.Extract(outputPackage)
            .Where(value => GameplayPath(value.Layer, value.Name) is null)
            .ToDictionary(value => (value.Layer, value.Name), value => value.Bytes);
        if (expectedBase.Count != actualBase.Count)
            throw new InvalidDataException(
                $"Packed base-layer inventory has {actualBase.Count} payloads; expected {expectedBase.Count}.");
        foreach (var expected in expectedBase)
        {
            if (!actualBase.TryGetValue(expected.Key, out var bytes))
                throw new InvalidDataException(
                    $"Packed output is missing staged {expected.Key.Item1} payload {expected.Key.Item2}.");
            if (!EquivalentPayload(expected.Value, bytes))
                throw new InvalidDataException(
                    $"Packed {expected.Key.Item1} payload {expected.Key.Item2} does not match its composed staging payload.");
        }
    }

    private sealed record ReplacementPlan(
        Dictionary<string, ReadOnlyMemory<byte>> Replacements,
        byte[]? ExpectedPrimaryTerrain);

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
        if (replacements.ContainsKey("assets/asset_header.bin")) sections.Add("level/sound-remap");
        if (replacements.ContainsKey("level_wad/sound.bnk")) sections.Add("level/sound-bank");
        if (replacements.ContainsKey("occlusion/occlusion.bin")) sections.Add("level/occlusion");
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

    internal static ReadOnlyMemory<byte>? ExpandPrimaryStreamBuffer(
        ReadOnlyMemory<byte>? replacement,
        ReadOnlyMemory<byte> source,
        int requiredLength)
    {
        var payload = replacement ?? source;
        if (requiredLength <= payload.Length) return replacement;
        var reserved = new byte[requiredLength];
        payload.Span.CopyTo(reserved);
        return reserved;
    }

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
