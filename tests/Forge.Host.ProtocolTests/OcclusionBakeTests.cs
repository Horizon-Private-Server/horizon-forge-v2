using RatchetPs2.Core.Occlusion;
using System.Numerics;
using System.Text.Json;
using Forge.Host.Domain.Occlusion;

internal static class OcclusionBakeTests
{
    internal static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-occlusion-bake-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var adjacent = OcclusionSamplePlanner.PlanCells([new(0, 0, 0, false), new(1, 0, 0, true)]);
            Equal(14, adjacent.Origins.Count, "adjacent cells share four corner cameras");
            Equal(4, adjacent.Cells[0].Samples.Intersect(adjacent.Cells[1].Samples).Count(), "shared corner sample IDs");
            LocalVisibilityChecks();
            SharedCornerChecks();
            Throws<InvalidDataException>(() => OcclusionSamplePlanner.PlanCells([new(0, 0, 0, false), new(0, 0, 0, true)]));
            var largeOctants = Enumerable.Range(0, 40_000).Select(i => new OcclusionOctant(i % 40, i / 40 % 40, i / 1600, false)).ToArray();
            var large = OcclusionSamplePlanner.PlanCells(largeOctants);
            Equal(40_000, large.Cells.Count, "stock-size maps exceed the old 20,000-cell probe limit");
            Equal(83_706, large.Origins.Count, "shared cameras exceed the old 40,000-origin probe limit");
            Throws<InvalidDataException>(() => OcclusionSamplePlanner.PlanCells(new OcclusionOctant[OcclusionBakeLimits.MaximumCells + 1]));
            Throws<InvalidDataException>(() => OcclusionSamplePlanner.PlanCells([new(250_001, 0, 0, false)]));
            Throws<InvalidDataException>(() => OcclusionSamplePlanner.PlanCells(
                Enumerable.Range(0, OcclusionBakeLimits.MaximumOrigins / 9 + 1).Select(i => new OcclusionOctant(i * 2, 0, 0, false)).ToArray()));
            var maximumEncodedMasks = ((long)OcclusionBakeLimits.MaximumOrigins * 256 + 2) / 3 * 4;
            Equal(true, maximumEncodedMasks + 4096 < OcclusionBakeLimits.MaximumResultBytes, "whole result fits the transfer budget");
            var largeRoot = Path.Combine(root, "large"); Directory.CreateDirectory(largeRoot);
            var largeScene = await OcclusionWorkerProtocol.WriteSceneAsync(largeRoot, new string('a', 64),
                [new(Vector3.Zero, Vector3.UnitX, Vector3.UnitY, 0, 3, Vector2.Zero, Vector2.UnitX, Vector2.UnitY, -1, 0, 1)], [],
                large.Origins, large.Cells, [], [new([0, 0, 0], [0, 0, 1], new string('0', 256), new string('0', 256))], new byte[128], CancellationToken.None);
            var largeRaw = await File.ReadAllBytesAsync(Path.Combine(largeRoot, "scene.json"));
            var largeResult = new OcclusionBakeResult(2, OcclusionWorkerProtocol.Profile, largeScene.InputHash, OcclusionWorkerProtocol.Hash(largeRaw),
                large.Origins.Count, large.Cells.Count, (large.Origins.Count + 255) / 256, 1, null,
                Convert.ToBase64String(new byte[large.Origins.Count * 256]));
            Equal(large.Cells.Count * 128, OcclusionWorkerProtocol.ValidateResult(largeScene, largeRaw, largeResult).Length, "large result union validates");
            var largeResultBytes = JsonSerializer.SerializeToUtf8Bytes(largeResult, OcclusionWorkerProtocol.Json);
            Equal(true, largeResultBytes.Length < 32 * 1024 * 1024, "sample-only transfer removes the redundant full-grid payload");
            var largeResultPath = Path.Combine(largeRoot, "result.json"); await File.WriteAllBytesAsync(largeResultPath, largeResultBytes);
            Equal(largeResultBytes.Length, (await OcclusionBakeStore.ReadBoundedAsync(largeResultPath, OcclusionBakeLimits.MaximumResultBytes, CancellationToken.None)).Length, "large result readback");
            var planned = OcclusionSamplePlanner.PlanCells([new(0, 0, 0, false)]);
            var origins = planned.Origins;
            var scene = await OcclusionWorkerProtocol.WriteSceneAsync(root, new string('a', 64),
                [new(Vector3.Zero, Vector3.UnitX, Vector3.UnitY, 7, 3, Vector2.Zero, Vector2.UnitX, Vector2.UnitY, 0, 0, 1)],
                [new(2, 1, [0, 128])], origins, planned.Cells, [],
                [new([2, 2, 2], [0, 0, 1], new string('0', 256), new string('0', 256))], new byte[128], CancellationToken.None, samplePullIn: 5, pixelEdgeAllowance: false);
            Equal(5, scene.SamplePullIn, "scene records qualification pull-in");
            Equal(false, scene.PixelEdgeAllowance, "scene records disabled pixel edge allowance");
            var triangles = await File.ReadAllBytesAsync(Path.Combine(root, "triangles.bin"));
            if (triangles.Length != 80 || BitConverter.ToInt32(triangles, 12) != 7 || BitConverter.ToSingle(triangles, 16) != 1 || BitConverter.ToUInt32(triangles, 76) != 1)
                throw new InvalidOperationException("GPU triangle packing changed.");
            var raw = await File.ReadAllBytesAsync(Path.Combine(root, "scene.json"));
            Equal(false, JsonSerializer.Deserialize<OcclusionScene>(raw, OcclusionWorkerProtocol.Json)!.PixelEdgeAllowance, "worker scene round-trips disabled pixel edge allowance");
            Equal(false, OcclusionWorkerProtocol.Hash(raw) == OcclusionWorkerProtocol.Hash(JsonSerializer.SerializeToUtf8Bytes(scene with { PixelEdgeAllowance = true }, OcclusionWorkerProtocol.Json)), "edge setting is bound to worker result identity");
            var legacyScene = JsonSerializer.SerializeToNode(scene, OcclusionWorkerProtocol.Json)!.AsObject();
            legacyScene.Remove("pixelEdgeAllowance");
            Equal(false, legacyScene.Deserialize<OcclusionScene>(OcclusionWorkerProtocol.Json)!.PixelEdgeAllowance, "scenes without an explicit edge allowance default to disabled");
            var samples = new byte[9 * 256]; samples[5 * 256] = 32; samples[7 * 256 + 128] = 64;
            var cells = new byte[128]; cells[0] = 96;
            var result = new OcclusionBakeResult(2, OcclusionWorkerProtocol.Profile, scene.InputHash, OcclusionWorkerProtocol.Hash(raw), 9, 1, 1, 100, null, Convert.ToBase64String(samples));
            Equal(true, OcclusionWorkerProtocol.ValidateResult(scene, raw, result).SequenceEqual(cells), "family union");
            var extendedOrigins = origins.Concat(new float[][] { [0,0,2], [4,0,2], [0,4,2], [4,4,2] }).ToArray();
            var extended = scene with { Origins = extendedOrigins, Cells = [planned.Cells[0] with { Samples = Enumerable.Range(0,13).ToArray() }] };
            var extendedRaw = JsonSerializer.SerializeToUtf8Bytes(extended, OcclusionWorkerProtocol.Json);
            var extendedSamples = new byte[13*256];
            samples.CopyTo(extendedSamples,0); extendedSamples[9*256+590/8] = (byte)(1<<(590%8));
            extendedSamples[12*256+128+615/8] = (byte)(1<<(615%8));
            var extendedCells = cells.ToArray(); extendedCells[590/8] |= (byte)(1<<(590%8)); extendedCells[615/8] |= (byte)(1<<(615%8));
            var extendedResult = result with { Origins = 13, SceneHash = OcclusionWorkerProtocol.Hash(extendedRaw),
                SampleMasks = Convert.ToBase64String(extendedSamples) };
            Equal(true, OcclusionWorkerProtocol.ValidateResult(extended, extendedRaw, extendedResult).SequenceEqual(extendedCells),
                "extra edge samples retain original hits and independently add both target families");
            foreach (var count in new[]{10,11,12,14})
                Throws<InvalidDataException>(() => OcclusionWorkerProtocol.ValidateResult(extended with {
                    Cells = [extended.Cells[0] with { Samples = Enumerable.Repeat(0,count).ToArray() }] }, extendedRaw, extendedResult));
            Throws<InvalidDataException>(() => OcclusionWorkerProtocol.ValidateResult(scene with { SamplePullIn = 6 }, raw, result));
            Throws<InvalidDataException>(() => OcclusionWorkerProtocol.ValidateResult(scene with { BarrierHeights = [float.NaN] }, raw, result));
            var outside = scene with { Origins = scene.Origins.Select(p => p.ToArray()).ToArray() };
            outside.Origins[planned.Cells[0].Samples[1]][0] = -.001f;
            var outsideBytes = JsonSerializer.SerializeToUtf8Bytes(outside, OcclusionWorkerProtocol.Json);
            Throws<InvalidDataException>(() => OcclusionWorkerProtocol.ValidateResult(outside, outsideBytes,
                result with { SceneHash = OcclusionWorkerProtocol.Hash(outsideBytes) }));
            Throws<InvalidDataException>(() => OcclusionWorkerProtocol.ValidateResult(scene, raw, result with { SceneHash = new string('b', 64) }));
            Throws<InvalidDataException>(() => OcclusionWorkerProtocol.ValidateResult(scene, raw, result with { SchemaVersion = 1 }));
            Throws<InvalidDataException>(() => OcclusionWorkerProtocol.ValidateResult(scene, raw, result with { SampleMasks = "" }));
            Throws<InvalidDataException>(() => OcclusionWorkerProtocol.ValidateResult(scene, raw, result with { SampleMasks = new string('!', result.SampleMasks.Length) }));
            Throws<InvalidDataException>(() => OcclusionWorkerProtocol.ValidateResult(scene, raw, result with { SampleMasks = Convert.ToBase64String(new byte[samples.Length + 1]) }));
            Throws<OperationCanceledException>(() => OcclusionWorkerProtocol.ValidateResult(scene, raw, result, new CancellationToken(true)));
            Throws<InvalidDataException>(() => OcclusionWorkerProtocol.ValidateResult(scene, raw, result with { Batches = 0 }));
            var native = new byte[] { 1, 2, 3, 4 };
            var cache = new OcclusionBakeCache(1, OcclusionWorkerProtocol.Profile, scene.InputHash, new string('c', 64), OcclusionWorkerProtocol.Hash(native), native, large.Cells.Count, 100, null, scene.SamplePullIn, scene.PixelEdgeAllowance);
            await OcclusionBakeStore.CommitAsync(root, cache, CancellationToken.None);
            Equal(true, (await OcclusionBakeStore.ReadForPreviewAsync(root,cache.OriginalGridHash,CancellationToken.None))!.NativeGrid.SequenceEqual(native), "viewport reads last valid bake independently of a new scene fingerprint");
            await ThrowsAsync<InvalidDataException>(() => OcclusionBakeStore.ReadForPreviewAsync(root,new string('f',64),CancellationToken.None));
            Equal(true, (await OcclusionBakeStore.ReadAsync(root, scene.InputHash, cache.OriginalGridHash, CancellationToken.None))!.NativeGrid.SequenceEqual(native), "cache reopen");
            Equal(5, (await OcclusionBakeStore.ReadAsync(root, scene.InputHash, cache.OriginalGridHash, CancellationToken.None))!.SamplePullIn, "cache records qualification pull-in");
            Equal(false, (await OcclusionBakeStore.ReadAsync(root, scene.InputHash, cache.OriginalGridHash, CancellationToken.None))!.PixelEdgeAllowance, "cache records disabled pixel edge allowance");
            var legacyCache = JsonSerializer.SerializeToNode(cache, OcclusionWorkerProtocol.Json)!.AsObject();
            legacyCache.Remove("pixelEdgeAllowance");
            Equal(true, legacyCache.Deserialize<OcclusionBakeCache>(OcclusionWorkerProtocol.Json)!.PixelEdgeAllowance, "older caches retain enabled edge allowance");
            await ThrowsAsync<InvalidDataException>(() => OcclusionBakeStore.CommitAsync(root, cache with { SamplePullIn = -1 }, CancellationToken.None));
            Equal(large.Cells.Count, (await OcclusionBakeStore.ReadAsync(root, scene.InputHash, cache.OriginalGridHash, CancellationToken.None))!.Octants, "large-map cache reopens");
            await ThrowsAsync<InvalidDataException>(() => OcclusionBakeStore.ReadAsync(root, new string('d', 64), cache.OriginalGridHash, CancellationToken.None));
            await ThrowsAsync<InvalidDataException>(() => OcclusionBakeStore.CommitAsync(root, cache with { NativeGridHash = new string('e', 64) }, CancellationToken.None));
            foreach (var outdated in new[] { "obsolete-profile", OcclusionWorkerProtocol.Profile + "-unexpected" })
                await ThrowsAsync<InvalidDataException>(() => OcclusionBakeStore.CommitAsync(root, cache with { Profile = outdated }, CancellationToken.None));
            foreach (var oldProfile in new[] { "raster1024-lod0-uniform-samples-no-feather-v20", "raster1024-lod0-inward-local-corners-no-feather-v19" })
            {
                var previousProfile = cache with { Profile = oldProfile };
                await ThrowsAsync<InvalidDataException>(() => OcclusionBakeStore.CommitAsync(root, previousProfile, CancellationToken.None));
                await File.WriteAllTextAsync(Path.Combine(root,"cache/occlusion/generated.json"), JsonSerializer.Serialize(previousProfile,OcclusionWorkerProtocol.Json));
                Equal(previousProfile.Profile, (await OcclusionBakeStore.ReadForPreviewAsync(root,cache.OriginalGridHash,CancellationToken.None))!.Profile,
                    "viewport retains the previous saved bake until the user rebuilds");
                await ThrowsAsync<InvalidDataException>(() => OcclusionBakeStore.ReadAsync(root,cache.InputHash,cache.OriginalGridHash,CancellationToken.None));
            }
            await OcclusionBakeStore.CommitAsync(root,cache,CancellationToken.None);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => OcclusionBakeStore.CommitAsync(root, cache with { WallMs = 200 }, cancelled.Token));
            Equal(100d, (await OcclusionBakeStore.ReadAsync(root, scene.InputHash, cache.OriginalGridHash, CancellationToken.None))!.WallMs, "failed/cancelled commit preserves previous result");
            if (!OperatingSystem.IsWindows())
            {
                var linked = Path.Combine(root, "linked"); Directory.CreateDirectory(linked);
                Directory.CreateSymbolicLink(Path.Combine(linked, "cache"), Path.Combine(root, "cache"));
                await ThrowsAsync<InvalidDataException>(() => OcclusionBakeStore.ReadAsync(linked, scene.InputHash, cache.OriginalGridHash, CancellationToken.None));
            }
        }
        finally { Directory.Delete(root, true); }
    }
    private static void SharedCornerChecks()
    {
        var octants = (from z in new[] { -1, 0 } from y in new[] { -1, 0 } from x in new[] { -1, 0 }
                       select new OcclusionOctant(x, y, z, false)).Append(new(3, 0, 0, false)).ToArray();
        var planned = OcclusionSamplePlanner.PlanCells(octants);
        var shared = Enumerable.Range(0, planned.Origins.Count).Single(i => planned.Origins[i].All(v => v == 0));
        Equal(8, planned.Cells.Count(c => c.Samples.Contains(shared)), "one shared corner belongs to eight octants");
        var scene = new OcclusionScene(1, OcclusionWorkerProtocol.Profile, new string('a', 64), 1, "", "", "",
            planned.Origins, planned.Cells, [], [], new string('0', 256));
        foreach (var familyOffset in new[] { 0, 128 })
            foreach (var group in new[] { 0, 31, 32, 567, 568, 1022 })
            {
                var samples = new byte[planned.Origins.Count * 256];
                samples[shared * 256 + familyOffset + group / 8] = (byte)(1 << (group % 8));
                var masks = OcclusionMaskCombiner.Combine(scene, samples);
                for (int cell = 0; cell < 8; cell++)
                    Equal(true, (masks[cell * 128 + group / 8] & (1 << (group % 8))) != 0, "single corner hit survives in every touching octant");
                Equal(false, (masks[8 * 128 + group / 8] & (1 << (group % 8))) != 0, "unrelated octant does not inherit the hit");
            }
    }
    private static void LocalVisibilityChecks()
    {
        var planned = OcclusionSamplePlanner.PlanCells([new(0,0,0,false),new(-1,0,0,true),new(1,0,0,true),new(0,-1,0,false),new(1,-1,0,false),new(1,1,0,false),new(-1,-1,0,false)]);
        var scene = new OcclusionScene(1, OcclusionWorkerProtocol.Profile, new string('a',64), 1, "", "", "", planned.Origins, planned.Cells, [], [], new string('0',256));
        var samples = new byte[planned.Origins.Count * 256];
        void Set(int cell, int group, bool moby = false) => samples[planned.Cells[cell].Samples[0] * 256 + (moby ? 128 : 0) + group / 8] |= (byte)(1 << (group % 8));
        foreach (var cell in new[] { 1, 2, 4 })
        {
            foreach (var group in new[] { 7, 31, 1022, 1023 }) Set(cell, group);
            Set(cell, 400, true);
        }
        Set(1, 9); Set(5, 10); Set(6, 10); Set(0, 11);
        var masks = OcclusionMaskCombiner.Combine(scene, samples);
        bool Has(int cell, int group) => (masks[cell * 128 + group / 8] & (1 << (group % 8))) != 0;
        Equal(true, Has(0, 11), "local visibility retained");
        foreach (var group in new[] { 7, 31, 400, 1022, 9, 10, 1023 })
            Equal(false, Has(0, group), "neighbor agreement cannot add local visibility");
        foreach (var group in new[] { 7, 31, 400, 1022 })
        {
            Equal(true, Has(1, group), "first neighbor retains its own hits");
            Equal(true, Has(2, group), "second neighbor retains its own hits");
        }
        Equal(false, Has(3, 7), "neighbor visibility does not propagate");
        Equal(true, Has(1, 1023), "local reserved bit retained");
        var reversed = OcclusionMaskCombiner.Combine(scene with { Cells = scene.Cells.Reverse().ToArray() }, samples);
        for (int i = 0; i < planned.Cells.Count; i++)
            Equal(true, masks.AsSpan(i * 128, 128).SequenceEqual(reversed.AsSpan((planned.Cells.Count - 1 - i) * 128, 128)), "sample union is independent of cell order");
    }
    private static void Equal<T>(T expected, T actual, string reason) { if (!Equals(expected, actual)) throw new InvalidOperationException(reason); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}"); }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}"); }
}
