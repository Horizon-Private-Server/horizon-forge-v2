using Forge.Host.Domain;

namespace Forge.Host.Games.UYA;

internal static class UyaAssetSourceCompatibility
{
    public static bool CanConvert(AssetSource source) => source.Region == "NTSC-U"
        && (source.Game == "DL" && source.Revision == "1.00"
            || source.Game == "GC" && source.Revision == "1.01"
            || source.Game == "RC1" && source.Revision == "1.00");
}
