using Forge.Host.Domain;
using RatchetPs2.Core.Textures;
using RatchetPs2.Core.Textures.Palettes;
using RatchetPs2.Core.Textures.Pif;
using RatchetPs2.Core.Wad.Models;
using RatchetPs2.Games.DL.Level;
using RatchetPs2.Games.UYA.Level;

namespace Forge.Host.ProtocolTests.Games.UYA;

internal static class UyaTextureQualification
{
    public static bool MatchesBaseColors(
        UyaLevelAssetSourceFiles source,
        DlAssetHeader header,
        byte[] assetWad,
        AssetKind kind,
        byte marker)
    {
        var (family, offset, count) = kind switch
        {
            AssetKind.Moby => ("moby", header.MobyTextureOffset, header.MobyTextureCount),
            AssetKind.Tie => ("tie", header.TieTextureOffset, header.TieTextureCount),
            AssetKind.Shrub => ("shrub", header.ShrubTextureOffset, header.ShrubTextureCount),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var definition = DlAssetReader.ReadTextureDefinitions(source.HeaderBytes, offset, count).Single();
        var texture = PifReader.Read(DlAssetReader.BuildAssetTexture(
            family, 0, definition, source.PaletteBytes, assetWad,
            header.TextureDataOffset, isSwizzled: false).PifBytes);
        var colors = texture.PixelData.Select(value =>
        {
            var paletteOffset = TextureConverter.DecodePaletteIndex(value) * 4;
            return new TextureColor(
                texture.PaletteData[paletteOffset],
                texture.PaletteData[paletteOffset + 1],
                texture.PaletteData[paletteOffset + 2],
                texture.PaletteData[paletteOffset + 3]);
        });
        return colors.SequenceEqual([
            new(marker, 0, 0, 128),
            new(0, marker, 0, 64),
            new(0, marker, 0, 64),
            new(marker, 0, 0, 128),
        ]);
    }
}
