using RatchetPs2.Games.UYA.Occlusion;
using RatchetPs2.Core.Occlusion;
using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
using Forge.Host.Domain;
using Forge.Host.Domain.Occlusion;
using Forge.Host.Games.UYA;
using RatchetPs2.Games.UYA.Level;

namespace Forge.Host.ProtocolTests.Games.UYA;

internal static class UyaOcclusionBakeTests
{
    internal static async Task RunAsync()
    {
        UyaOcclusionVisibilityTests.Run();
        var root = Path.Combine(Path.GetTempPath(), $"forge-native-occlusion-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var workspace = await ForgeProjectWorkspace.CreateAsync(Path.Combine(root, "project"), "Native occlusion",
                new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"), new("UYA", "NTSC-U", "1.00", 41, new string('a', 32)), []);
            var job = Path.Combine(root, "job"); Directory.CreateDirectory(job);
            var grid = new byte[0xc0];
            BinaryPrimitives.WriteInt32LittleEndian(grid, 0x30);
            BinaryPrimitives.WriteUInt16LittleEndian(grid.AsSpan(4), 1); BinaryPrimitives.WriteUInt16LittleEndian(grid.AsSpan(6), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(grid.AsSpan(8), 3); BinaryPrimitives.WriteUInt16LittleEndian(grid.AsSpan(12), 2);
            BinaryPrimitives.WriteUInt16LittleEndian(grid.AsSpan(14), 1); BinaryPrimitives.WriteUInt16LittleEndian(grid.AsSpan(16), 5);
            BinaryPrimitives.WriteUInt16LittleEndian(grid.AsSpan(20), 3); BinaryPrimitives.WriteUInt16LittleEndian(grid.AsSpan(22), 1);
            var cameras = OcclusionSamplePlanner.PlanCells(UyaOcclusionGridReader.Read(grid).Octants
                .Select(c => new OcclusionOctant(c.X, c.Y, c.Z, false)).ToArray());
            var relocated = cameras.Origins[cameras.Cells[0].Samples[1]];
            relocated[0] += .125f; relocated[2] += .5f;
            var fallback = new byte[128]; fallback[229 / 8] = 1 << (229 % 8);
            var scene = new OcclusionScene(1, OcclusionWorkerProtocol.Profile, new string('b', 64), 1, new string('c', 64), new string('d', 64), new string('e', 64),
                cameras.Origins, cameras.Cells, [], [], Convert.ToHexStringLower(fallback), PixelEdgeAllowance: false);
            var sceneBytes = JsonSerializer.SerializeToUtf8Bytes(scene, OcclusionWorkerProtocol.Json);
            await File.WriteAllBytesAsync(Path.Combine(job, "scene.json"), sceneBytes);
            await File.WriteAllBytesAsync(Path.Combine(job, "original-grid.bin"), grid);
            await File.WriteAllTextAsync(Path.Combine(job, "project-fingerprint.txt"), workspace.CurrentFingerprint);
            var bottomCorner = cameras.Cells[0].Samples[1];
            var samples = new byte[cameras.Origins.Count * 256]; samples[bottomCorner * 256 + 1] = 4;
            var cells = new byte[cameras.Cells.Count * 128]; cells[1] = 4; cells[229 / 8] |= 1 << (229 % 8);
            foreach (var (group, family) in new[] { (567, 0), (568, 128), (1022, 0) })
            {
                samples[bottomCorner * 256 + family + group / 8] |= (byte)(1 << (group % 8));
                cells[group / 8] |= (byte)(1 << (group % 8));
            }
            var result = new OcclusionBakeResult(2, OcclusionWorkerProtocol.Profile, scene.InputHash, OcclusionWorkerProtocol.Hash(sceneBytes), cameras.Origins.Count,
                cameras.Cells.Count, 1, 1, null, Convert.ToBase64String(samples));
            var path = Path.Combine(job, "result.json");
            await File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(result, OcclusionWorkerProtocol.Json));
            await UyaOcclusionBakeService.CommitAsync(workspace.RootPath, job, CancellationToken.None);
            var cache = (await OcclusionBakeStore.ReadAsync(workspace.RootPath, scene.InputHash, OcclusionWorkerProtocol.Hash(grid), CancellationToken.None))!;
            if (cache.PixelEdgeAllowance) throw new Exception("Native publication lost the disabled pixel edge allowance setting.");
            var decoded = UyaOcclusionGridReader.Read(cache.NativeGrid);
            if (decoded.Octants.Count != cameras.Cells.Count || cache.NativeGrid[decoded.MasksOffset + 1] != 4
                || cache.NativeGrid[decoded.MasksOffset + 229 / 8] != fallback[229 / 8]) throw new Exception("Native readback failed.");
            if (!cache.NativeGrid.AsSpan(decoded.MasksOffset, 128).SequenceEqual(cells))
                throw new Exception("A single bottom-corner hit was lost while writing native occlusion masks.");
            var cachePath = Path.Combine(workspace.RootPath, "cache/occlusion/generated.json"); var before = await File.ReadAllBytesAsync(cachePath);
            await File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(result with { Octants = 0 }, OcclusionWorkerProtocol.Json));
            await ThrowsAsync<InvalidDataException>(() => UyaOcclusionBakeService.CommitAsync(workspace.RootPath, job, CancellationToken.None));
            var after = await File.ReadAllBytesAsync(cachePath);
            if (!before.SequenceEqual(after)) throw new Exception("Failed commit replaced previous native output.");
            workspace.Rename("Changed while baking"); await workspace.SaveAsync();
            await ThrowsAsync<InvalidDataException>(() => UyaOcclusionBakeService.CommitAsync(workspace.RootPath, job, CancellationToken.None));
            await VerifyCompactedCommitAsync(root);
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task VerifyCompactedCommitAsync(string root)
    {
        var workspace = await ForgeProjectWorkspace.CreateAsync(Path.Combine(root, "compacted"), "Compacted occlusion",
            new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"), new("UYA", "NTSC-U", "1.00", 42, new string('a', 32)), []);
        var job = Path.Combine(root, "compacted-job"); Directory.CreateDirectory(job);
        var original = new byte[192];
        void U16(int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(original.AsSpan(at), value);
        BinaryPrimitives.WriteInt32LittleEndian(original, 32);
        U16(6, 1); U16(8, 3); U16(14, 1); U16(16, 5); U16(22, 2);
        var cameras = OcclusionSamplePlanner.PlanCells([new(0, 0, 0, false), new(1, 0, 0, false)]);
        var scene = new OcclusionScene(1, OcclusionWorkerProtocol.Profile, new string('b', 64), 1,
            new string('c', 64), new string('d', 64), new string('e', 64), cameras.Origins, cameras.Cells, [], [], new string('0', 256));
        var sceneBytes = JsonSerializer.SerializeToUtf8Bytes(scene, OcclusionWorkerProtocol.Json);
        var samples = new byte[cameras.Origins.Count * 256]; var cells = new byte[256];
        for (var i = 0; i < 2; i++)
        {
            var group = 10 + i;
            samples[cameras.Cells[i].Samples[0] * 256 + group / 8] |= (byte)(1 << (group % 8));
            cells[i * 128 + group / 8] |= (byte)(1 << (group % 8));
        }
        var result = new OcclusionBakeResult(2, OcclusionWorkerProtocol.Profile, scene.InputHash,
            OcclusionWorkerProtocol.Hash(sceneBytes), cameras.Origins.Count, 2, 1, 1, null,
            Convert.ToBase64String(samples));
        await File.WriteAllBytesAsync(Path.Combine(job, "scene.json"), sceneBytes);
        await File.WriteAllBytesAsync(Path.Combine(job, "original-grid.bin"), original);
        await File.WriteAllTextAsync(Path.Combine(job, "project-fingerprint.txt"), workspace.CurrentFingerprint);
        await File.WriteAllBytesAsync(Path.Combine(job, "result.json"), JsonSerializer.SerializeToUtf8Bytes(result, OcclusionWorkerProtocol.Json));
        await UyaOcclusionBakeService.CommitAsync(workspace.RootPath, job, CancellationToken.None);
        var cache = (await OcclusionBakeStore.ReadAsync(workspace.RootPath, scene.InputHash,
            OcclusionWorkerProtocol.Hash(original), CancellationToken.None))!;
        var decoded = UyaOcclusionGridReader.Read(cache.NativeGrid);
        if (cache.NativeGrid.Length > original.Length || decoded.Octants.Select(c => c.MaskIndex).Distinct().Count() != 1
            || cache.NativeGrid[decoded.MasksOffset + 1] != 12)
            throw new Exception("Native publication must fit the source allocation and preserve both cells' generated visibility.");
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}"); }
}
