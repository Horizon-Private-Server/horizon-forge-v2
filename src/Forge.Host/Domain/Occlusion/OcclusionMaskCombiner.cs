using System.Runtime.InteropServices;

namespace Forge.Host.Domain.Occlusion;

internal static class OcclusionMaskCombiner
{
    internal static byte[] Combine(OcclusionScene scene, byte[] samples, CancellationToken token = default)
    {
        if (samples.Length != scene.Origins.Count * 256)
            throw new InvalidDataException("Occlusion mask matrix is incomplete.");
        var fallback = Convert.FromHexString(scene.FallbackMask);
        var sampleWords = MemoryMarshal.Cast<byte, ulong>(samples);
        var result = new byte[scene.Cells.Count * 128];
        var coordinates = new HashSet<(int, int, int)>(scene.Cells.Count);
        for (int i = 0; i < scene.Cells.Count; i++)
        {
            if ((i & 255) == 0) token.ThrowIfCancellationRequested();
            var cell = scene.Cells[i];
            if (!coordinates.Add((cell.X, cell.Y, cell.Z)))
                throw new InvalidDataException("Occlusion contains duplicate octant coordinates.");
            var mask = result.AsSpan(i * 128, 128); fallback.CopyTo(mask);
            var words = MemoryMarshal.Cast<byte, ulong>(mask);
            foreach (var id in cell.Samples)
                for (int word = 0; word < 16; word++)
                    words[word] |= sampleWords[id * 32 + word] | sampleWords[id * 32 + 16 + word];
            mask[127] = (byte)((mask[127] & 127) | (cell.ReservedVisible ? 128 : 0));
        }
        return result;
    }
}
