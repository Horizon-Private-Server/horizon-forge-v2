using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using RatchetPs2.Core.Occlusion;

namespace Forge.Host.Domain.Occlusion;

public static class OcclusionWorkerProtocol
{
    public const string Profile = "raster1024-lod0-uniform-samples-no-feather-v21";
    public const float BackfaceExitDistance = 1.5f;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static async Task<OcclusionScene> WriteSceneAsync(string directory, string inputHash,
        IReadOnlyList<OcclusionGpuTriangle> triangles, IReadOnlyList<OcclusionGpuTexture> textures,
        IReadOnlyList<float[]> origins, IReadOnlyList<OcclusionCell> cells,
        IReadOnlyList<OcclusionGpuSphere> spheres, IReadOnlyList<OcclusionGpuCheck> checks, byte[] fallback,
        CancellationToken token, int samplePullIn = 0, IReadOnlyList<float>? barrierHeights = null, bool pixelEdgeAllowance = false)
    {
        if (samplePullIn is < 0 or > 5) throw new ArgumentOutOfRangeException(nameof(samplePullIn));
        if (!ValidBarriers(barrierHeights) || triangles.Count is < 1 or > 1_000_000 || textures.Count > 4096
            || origins.Count is < 1 or > OcclusionBakeLimits.MaximumOrigins || cells.Count is < 1 or > OcclusionBakeLimits.MaximumCells
            || spheres.Count > 100000 || checks.Count is < 1 or > 16 || fallback.Length != 128
            || textures.Sum(t => (long)t.Alpha.Length) > 64 * 1024 * 1024)
            throw new InvalidDataException("Occlusion input exceeds the supported resource budget.");
        // This is Forge's GPU transfer format; native decoding remains in the SDK adapter.
        var data = new byte[checked(triangles.Count * 80)];
        for (int i = 0; i < triangles.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var t = triangles[i];
            void F(int offset, float value) => BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(i * 80 + offset), value);
            void U(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(i * 80 + offset), value);
            var e1 = t.B - t.A; var e2 = t.C - t.A;
            F(0, t.A.X); F(4, t.A.Y); F(8, t.A.Z); U(12, unchecked((uint)t.Group));
            F(16, e1.X); F(20, e1.Y); F(24, e1.Z); U(28, t.Flags);
            F(32, e2.X); F(36, e2.Y); F(40, e2.Z); U(44, unchecked((uint)t.Texture));
            F(48, t.UvA.X); F(52, t.UvA.Y); F(56, t.UvB.X); F(60, t.UvB.Y);
            F(64, t.UvC.X); F(68, t.UvC.Y); U(72, t.WrapFlags); U(76, t.Rank);
        }
        var descriptors = new byte[textures.Count * 16];
        var count = textures.Sum(t => t.Alpha.Length); var alpha = new byte[(count + 3) / 4 * 4]; var cursor = 0;
        for (int i = 0; i < textures.Count; i++)
        {
            var t = textures[i];
            if (t.Width is < 1 or > 4096 || t.Height is < 1 or > 4096 || (long)t.Width * t.Height != t.Alpha.Length)
                throw new InvalidDataException("Invalid occlusion alpha texture.");
            BinaryPrimitives.WriteInt32LittleEndian(descriptors.AsSpan(i * 16), cursor);
            BinaryPrimitives.WriteInt32LittleEndian(descriptors.AsSpan(i * 16 + 4), t.Width);
            BinaryPrimitives.WriteInt32LittleEndian(descriptors.AsSpan(i * 16 + 8), t.Height);
            t.Alpha.CopyTo(alpha, cursor); cursor += t.Alpha.Length;
        }
        var scene = new OcclusionScene(1, Profile, inputHash, triangles.Count, Hash(data), Hash(descriptors), Hash(alpha), origins, cells, spheres, checks, Convert.ToHexStringLower(fallback), samplePullIn, barrierHeights, pixelEdgeAllowance);
        await File.WriteAllBytesAsync(Path.Combine(directory, "triangles.bin"), data, token);
        await File.WriteAllBytesAsync(Path.Combine(directory, "textures.bin"), descriptors, token);
        await File.WriteAllBytesAsync(Path.Combine(directory, "alpha.bin"), alpha, token);
        await File.WriteAllBytesAsync(Path.Combine(directory, "scene.json"), JsonSerializer.SerializeToUtf8Bytes(scene, Json), token);
        return scene;
    }

    public static byte[] ValidateResult(OcclusionScene scene, byte[] sceneBytes, OcclusionBakeResult result, CancellationToken token = default)
    {
        if (scene.SchemaVersion != 1 || scene.Profile != Profile || !ValidBarriers(scene.BarrierHeights) || scene.SamplePullIn is < 0 or > 5 || scene.Origins is null || scene.Origins.Count is < 1 or > OcclusionBakeLimits.MaximumOrigins
            || scene.Cells is null || scene.Cells.Count is < 1 or > OcclusionBakeLimits.MaximumCells || scene.Cells.Any(c => c is null || c.Samples is null
                || c.Samples.Count is not (9 or 13) || c.Samples.Any(id => id < 0 || id >= scene.Origins.Count) || !ValidCameras(c, scene.Origins))
            || scene.FallbackMask is null || scene.FallbackMask.Length != 256 || !scene.FallbackMask.All(Uri.IsHexDigit)
            || result.SchemaVersion != 2 || result.Profile != Profile || result.SceneHash != Hash(sceneBytes)
            || result.InputHash != scene.InputHash || result.Origins != scene.Origins.Count || result.Octants != scene.Cells.Count
            || result.Batches != (scene.Origins.Count + 255) / 256 || !double.IsFinite(result.WallMs) || result.WallMs < 0
            || result.GpuMs is { } gpu && (!double.IsFinite(gpu) || gpu < 0))
            throw new InvalidDataException("Occlusion bake is incomplete or belongs to a different scene.");
        token.ThrowIfCancellationRequested();
        if (result.SampleMasks is null || result.SampleMasks.Length != (scene.Origins.Count * 256 + 2) / 3 * 4)
            throw new InvalidDataException("Occlusion mask matrix is incomplete.");
        byte[] samples;
        try { samples = Convert.FromBase64String(result.SampleMasks); }
        catch (FormatException e) { throw new InvalidDataException("Occlusion masks are malformed.", e); }
        if (samples.Length != scene.Origins.Count * 256)
            throw new InvalidDataException("Occlusion mask matrix is incomplete.");
        return OcclusionMaskCombiner.Combine(scene, samples, token);
    }

    private static bool ValidCameras(OcclusionCell cell, IReadOnlyList<float[]> origins)
    {
        var minimum = new[] { cell.X * 4d, cell.Y * 4d, cell.Z * 4d };
        for (int i = 0; i < cell.Samples.Count; i++)
        {
            var point = origins[cell.Samples[i]];
            if (point is null || point.Length != 3) return false;
            for (int a = 0; a < 3; a++)
                if (!float.IsFinite(point[a]) || Math.Abs(point[a]) > 1000000 || (i == 0
                    ? point[a] != minimum[a] + 2 : point[a] < minimum[a] || point[a] > minimum[a] + 4)) return false;
        }
        return true;
    }

    private static bool ValidBarriers(IReadOnlyList<float>? heights) => heights is null
        || heights.Count <= 16 && heights.All(h => float.IsFinite(h) && Math.Abs(h) <= 1000000)
            && heights.Distinct().Count() == heights.Count;
}
