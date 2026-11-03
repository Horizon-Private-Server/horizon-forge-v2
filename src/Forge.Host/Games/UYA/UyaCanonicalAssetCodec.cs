using Forge.Host.Domain;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.LevelAssets;

namespace Forge.Host.Games.UYA;

internal static class UyaCanonicalAssetCodec
{
    public static byte[] Encode(ReadOnlySpan<byte> definition, ReadOnlySpan<byte> model,
        IReadOnlyList<FrontendAssetTexture> textures) => CatalogModelCodec.Encode(GameId.UYA, definition, model, textures);

    public static CatalogModel Decode(ReadOnlySpan<byte> bytes)
    {
        var model = CatalogModelCodec.Decode(bytes);
        if (model.Game != GameId.UYA) throw new InvalidDataException("Asset blob is not a UYA canonical model.");
        return model;
    }

    public static bool IsValid(ReadOnlySpan<byte> bytes)
    {
        try { _ = Decode(bytes); return true; }
        catch (Exception exception) when (exception is InvalidDataException or OverflowException) { return false; }
    }
}
