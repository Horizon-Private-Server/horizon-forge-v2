using Forge.Host.Domain;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.IO;
using RatchetPs2.Core.LevelAssets;
using RatchetPs2.Core.Tfrags;
using RatchetPs2.Core.Wad;
using RatchetPs2.Games.UYA.Level;

namespace Forge.Host.Games.UYA;

internal static class UyaBaseLayerService
{
    public static UyaBaseLayerPayload? ExtractSky(UyaLevelWadPackage package)
    {
        var source = UyaLevelWadRenderPackageBuilder.ReadAssetSourceFiles(package.Files);
        var header = LevelAssetReader.ReadHeader(source.HeaderBytes);
        var assetBytes = BinaryMagic.IsWad(source.AssetWadBytes)
            ? WadCompression.Decompress(source.AssetWadBytes)
            : source.AssetWadBytes;
        var offsets = LevelAssetReader.CollectKnownAssetOffsets(
            header,
            assetBytes.Length,
            LevelAssetReader.ReadModelDefinitions(source.HeaderBytes, header.MobyModelOffset, header.MobyModelCount),
            LevelAssetReader.ReadModelDefinitions(source.HeaderBytes, header.TieModelOffset, header.TieModelCount),
            LevelAssetReader.ReadShrubDefinitions(source.HeaderBytes, header.ShrubModelOffset, header.ShrubModelCount),
            [header.SceneViewSize]);
        var bytes = LevelAssetReader.ReadAssetSlice(assetBytes, header.SkyOffset, offsets);
        return bytes.Length == 0 ? null : new(
            BakeLayerId.Sky, "sky.bin", AssetKind.Sky, "level_wad/assets/asset_wad.bin", 0, bytes);
    }

    public static IReadOnlyList<UyaBaseLayerPayload> Extract(UyaLevelWadPackage package)
    {
        var files = package.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        var source = UyaLevelWadRenderPackageBuilder.ReadAssetSourceFiles(package.Files);
        var header = LevelAssetReader.ReadHeader(source.HeaderBytes);
        var assetBytes = BinaryMagic.IsWad(source.AssetWadBytes)
            ? WadCompression.Decompress(source.AssetWadBytes)
            : source.AssetWadBytes;
        var mobys = LevelAssetReader.ReadModelDefinitions(source.HeaderBytes, header.MobyModelOffset, header.MobyModelCount);
        var ties = LevelAssetReader.ReadModelDefinitions(source.HeaderBytes, header.TieModelOffset, header.TieModelCount);
        var shrubs = LevelAssetReader.ReadShrubDefinitions(source.HeaderBytes, header.ShrubModelOffset, header.ShrubModelCount);
        var offsets = LevelAssetReader.CollectKnownAssetOffsets(
            header, assetBytes.Length, mobys, ties, shrubs, [header.SceneViewSize]);
        var payloads = new List<UyaBaseLayerPayload>();

        Add(payloads, BakeLayerId.World, "level-settings.bin", AssetKind.World,
            "gameplay/core/level_settings.bin", 0,
            files.GetValueOrDefault("gameplay/core/level_settings.bin")?.Bytes ?? []);
        Add(payloads, BakeLayerId.Sky, "sky.bin", AssetKind.Sky,
            "level_wad/assets/asset_wad.bin", 0,
            LevelAssetReader.ReadAssetSlice(assetBytes, header.SkyOffset, offsets));
        Add(payloads, BakeLayerId.Tfrags, "primary.bin", AssetKind.Tfrag,
            "level_wad/assets/asset_wad.bin", 0,
            LevelAssetReader.ReadAssetSlice(assetBytes, header.TerrainOffset, offsets));
        Add(payloads, BakeLayerId.Collision, "collision.bin", AssetKind.Collision,
            "level_wad/assets/asset_wad.bin", 0,
            LevelAssetReader.ReadAssetSlice(assetBytes, header.CollisionOffset, offsets));
        Add(payloads, BakeLayerId.Lighting, "directional-lights.bin", AssetKind.Lighting,
            "gameplay/core/directional_lights.bin", 0,
            files.GetValueOrDefault("gameplay/core/directional_lights.bin")?.Bytes ?? []);
        Add(payloads, BakeLayerId.Lighting, "point-lights.bin", AssetKind.Lighting,
            "gameplay/core/point_lights.bin", 1,
            files.GetValueOrDefault("gameplay/core/point_lights.bin")?.Bytes ?? []);
        Add(payloads, BakeLayerId.Lighting, "tie-ambient-rgbas.bin", AssetKind.Lighting,
            "gameplay/core/tie_ambient_rgbas.bin", 2,
            files.GetValueOrDefault("gameplay/core/tie_ambient_rgbas.bin")?.Bytes ?? []);

        for (var index = 1; index < package.LevelWad.Chunks.Count; index++)
        {
            if (!files.TryGetValue($"level_wad/chunks/chunk{index}.wad", out var chunk)) continue;
            Add(payloads, BakeLayerId.Tfrags, $"chunk-{index}.bin", AssetKind.Tfrag,
                $"level_wad/chunks/chunk{index}.wad", index,
                TfragChunkWadReader.ReadTerrainPayload(chunk.Bytes));
            Add(payloads, BakeLayerId.Collision, $"chunk-{index}.bin", AssetKind.Collision,
                $"level_wad/chunks/chunk{index}.wad", index,
                TfragChunkWadReader.ReadCollisionPayload(chunk.Bytes));
        }
        return payloads;
    }

    public static IReadOnlyList<AssetCatalogPut> CreateCatalogPuts(
        IReadOnlyList<UyaBaseLayerPayload> payloads,
        OpaqueContentSource source,
        string importerVersion) => payloads.Select(payload =>
        {
            var aliases = new List<string>
            {
                $"base:{payload.Layer.ToString().ToLowerInvariant()}:{Path.GetFileNameWithoutExtension(payload.Name)}",
            };
            if (payload.Kind == AssetKind.Sky) aliases.AddRange(UyaSkyShellIndexService.Aliases(payload.Bytes));
            return new AssetCatalogPut(
                payload.Kind,
                UyaBaseLayerSchema.CanonicalFormatVersion,
                payload.Bytes,
                new(
                    importerVersion,
                    new(source.Game, source.Region, source.Revision, $"level{source.Level:00}",
                        payload.SourceArchive, payload.SourceIndex, source.Fingerprint),
                    aliases,
                    ["vanilla", $"game:{source.Game}", $"level:{source.Level:00}", "base-layer"]));
        }).ToArray();

    private static void Add(
        ICollection<UyaBaseLayerPayload> payloads,
        BakeLayerId layer,
        string name,
        AssetKind kind,
        string sourceArchive,
        int sourceIndex,
        byte[] bytes)
    {
        if (bytes.Length > 0) payloads.Add(new(layer, name, kind, sourceArchive, sourceIndex, bytes));
    }
}
