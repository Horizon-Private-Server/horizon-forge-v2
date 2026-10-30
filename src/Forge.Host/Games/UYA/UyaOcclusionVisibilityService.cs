using RatchetPs2.Games.UYA.Occlusion;
using System.Text.Json;
using Forge.Host.Domain;
using Forge.Host.Domain.Occlusion;
using RatchetPs2.Core.Games;
using RatchetPs2.Games.UYA.Gameplay;
using RatchetPs2.Games.UYA.Level;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.UYA;

internal static class UyaOcclusionVisibilityService
{
    internal static async Task ExportAsync(string projectRoot, string sourceIso, string directory, CancellationToken token)
    {
        var workspace = await ForgeProjectWorkspace.OpenAsync(projectRoot, token);
        if (workspace.Manifest.Target.Game != "UYA") throw new NotSupportedException("Occlusion preview currently supports UYA.");
        var imported = await UyaOcclusionImportPayload.ReadAsync(workspace, token);
        byte[] gridBytes, mappingBytes;
        if (imported is not null) { gridBytes = imported.AssetGrid; mappingBytes = imported.GameplayMappings; }
        else
        {
            await using var iso = new FileStream(sourceIso, FileMode.Open, FileAccess.Read, FileShare.Read);
            var wad = await Task.Run(() => LevelArchiveReader.ExtractPrimary(GameId.UYA, iso, workspace.Manifest.BaseLevel.Level), token);
            var files = UyaLevelWadUnpacker.Unpack(wad).Files;
            var source = UyaLevelWadRenderPackageBuilder.ReadAssetSourceFiles(files);
            gridBytes = UyaOcclusionGridReader.ReadLevelAssetPayload(source.HeaderBytes, source.AssetWadBytes);
            mappingBytes = files.Single(f => f.Path == "gameplay/core/occlusion.bin").Bytes;
        }
        var original = UyaOcclusionGridReader.Read(gridBytes, OcclusionBakeLimits.MaximumCells, token);
        var cache = await OcclusionBakeStore.ReadForPreviewAsync(projectRoot, OcclusionWorkerProtocol.Hash(gridBytes), token);
        var bytes = cache is null ? gridBytes
            : await Task.Run(() => OcclusionComposer.CompactGrid(GameId.UYA, gridBytes, cache.NativeGrid, token), token);
        var grid = UyaOcclusionGridReader.Read(bytes, OcclusionBakeLimits.MaximumCells, token);
        OcclusionComposer.ValidateGrid(GameId.UYA, gridBytes, bytes, original.Octants.Count, token);
        var mappings = UyaOcclusionMappingsReader.Read(mappingBytes, 1_000_000, token);
        var entities = LinkEntities(workspace.Content.Entities, mappings, token);
        var masks = bytes.AsSpan(grid.MasksOffset, checked((grid.Octants.Select(c => c.MaskIndex).DefaultIfEmpty(-1).Max() + 1) * 128)).ToArray();
        var data = new OcclusionVisibilityData(1, cache is null ? "vanilla" : "generated", OcclusionWorkerProtocol.Hash(bytes),
            grid.Octants.Select(c => new OcclusionVisibilityOctant(c.X, c.Y, c.Z, c.MaskIndex)).ToArray(), masks, entities);
        await File.WriteAllBytesAsync(Path.Combine(directory, "visibility.json"), JsonSerializer.SerializeToUtf8Bytes(data, OcclusionWorkerProtocol.Json), token);
    }

    internal static IReadOnlyList<OcclusionVisibilityEntity> LinkEntities(IReadOnlyList<ProjectEntity> entities,
        UyaOcclusionMappings mappings, CancellationToken token = default)
    {
        var ties = mappings.Ties.GroupBy(m => m.OcclusionId).ToDictionary(g => g.Key, g => g.Select(m => m.BitIndex).Distinct().ToArray());
        var mobys = mappings.Mobys.GroupBy(m => m.OcclusionId).ToDictionary(g => g.Key, g => g.Select(m => m.BitIndex).Distinct().ToArray());
        var result = new List<OcclusionVisibilityEntity>();
        foreach (var entity in entities)
        {
            token.ThrowIfCancellationRequested();
            int[]? groups = null;
            if (entity.Tfrag is { OcclusionIndexStash: 0 } tfrag && tfrag.OcclusionIndex >= 0 && tfrag.OcclusionIndex < mappings.Tfrags.Count)
                groups = [mappings.Tfrags[tfrag.OcclusionIndex].BitIndex];
            else if (entity.Layer == "ties" && entity.Source?.RawRecord.Length == UyaTieInstancesReader.RecordSize)
                ties.TryGetValue(UyaTieInstancesReader.ReadInstance(entity.Source.RawRecord).OcclusionId, out groups);
            else if (entity.Layer == "mobys" && entity.Source?.RawRecord.Length == UyaMobyInstancesReader.RecordSize)
                mobys.TryGetValue(UyaMobyInstancesReader.ReadInstance(entity.Source.RawRecord).Uid, out groups);
            // Unknown/new objects and the reserved group remain visible in the editor.
            if (groups is { Length: > 0 } && groups.All(g => g is >= 0 and < 1023))
                result.Add(new(entity.EntityId.ToString(), groups));
        }
        return result;
    }
}
