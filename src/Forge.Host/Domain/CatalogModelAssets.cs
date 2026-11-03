using RatchetPs2.Core.Games;
using RatchetPs2.Core.LevelAssets;

namespace Forge.Host.Domain;

internal static class CatalogModelAssets
{
    private static readonly byte[] MissingTexturePif = CatalogTextureDefaults.CreateMissingTexturePif();

    public static IReadOnlyList<AssetCatalogPut> Create(
        GameId game,
        uint canonicalFormatVersion,
        ExtractedLevelAsset asset,
        AssetSource source,
        string importer,
        IReadOnlyList<string> modelAliases)
    {
        var kind = asset.Kind switch
        {
            FrontendAssetKind.Moby => AssetKind.Moby,
            FrontendAssetKind.Tie => AssetKind.Tie,
            FrontendAssetKind.Shrub => AssetKind.Shrub,
            _ => throw new ArgumentOutOfRangeException(nameof(asset), "Unsupported model asset kind."),
        };
        var kindName = kind.ToString().ToLowerInvariant();
        string[] tags = ["vanilla", $"game:{source.Game}", $"level:{source.Level[5..]}"];
        var results = new List<AssetCatalogPut>(asset.Textures.Count + 1);
        if (asset.ModelBytes.Length > 0)
            results.Add(new(kind, canonicalFormatVersion,
                CatalogModelCodec.Encode(game, asset.DefinitionBytes, asset.ModelBytes,
                    asset.Textures.Select(texture => new FrontendAssetTexture(
                        texture.Role, texture.PifBytes ?? MissingTexturePif)).ToArray()),
                new(importer, source, modelAliases, asset.Textures.Any(texture => texture.PifBytes is null)
                    ? [.. tags, "texture:placeholder"] : tags)));

        var roleSlots = new Dictionary<byte, int>();
        foreach (var texture in asset.Textures)
        {
            var slot = roleSlots.GetValueOrDefault(texture.Role);
            roleSlots[texture.Role] = slot + 1;
            var role = texture.Role switch
            {
                0 => "material",
                1 => "billboard",
                _ => $"role:{texture.Role}",
            };
            var restorable = texture.PifBytes is not null;
            results.Add(new(AssetKind.Texture, ProjectTextureAssetSchema.CanonicalFormatVersion,
                texture.PifBytes ?? MissingTexturePif,
                new(importer, source with
                {
                    MobySounds = null, TextureUse = new(kind, asset.ClassId, role, slot, restorable),
                }, [$"{kindName}:0x{asset.ClassId:X4} {role} {slot}"],
                    restorable ? tags : [.. tags, "texture:placeholder"])));
        }
        return results;
    }
}
