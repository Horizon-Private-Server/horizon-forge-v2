using RatchetPs2.Core.Textures.Pif;

namespace Forge.Host.Domain;

internal static class CatalogTextureDefaults
{
    public static byte[] CreateMissingTexturePif()
    {
        const int size = 8;
        var palette = new byte[0x400];
        new byte[] { 255, 0, 255, 255, 0, 0, 0, 255 }.CopyTo(palette, 0);
        var pixels = new byte[size * size];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
            pixels[y * size + x] = (byte)(((x / 2) + (y / 2)) & 1);
        return PifWriter.Write(PifWriter.CreateIndexed8(size, size, palette, pixels));
    }

}
