using Forge.Host.Domain;
using RatchetPs2.Core.Textures.Palettes;

namespace Forge.Host.Games.UYA;

public static class UyaTextureInventoryService
{
    public static async Task<TextureInventory> BuildAsync(
        string projectRoot,
        AssetCatalogStore catalog,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var staging = await BakeStagingStore.OpenAsync(projectRoot, cancellationToken);
        var inputs = new List<TextureInventoryInput>();
        foreach (var layer in UyaStaticLayerSchema.Layers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = staging.Manifest.Layers.SingleOrDefault(value => value.Layer == layer)
                ?? throw new InvalidDataException($"Staged {layer} output is missing; run the bake before texture inventory.");
            var root = ForgeProjectPersistence.ResolveRelativePath(staging.RootPath, snapshot.RelativePath);
            var manifest = ForgeProjectPersistence.Deserialize<UyaStaticBakeManifest>(
                await File.ReadAllBytesAsync(Path.Combine(root, "manifest.json"), cancellationToken),
                $"staged {layer} layer");
            if (manifest.Layer != layer)
                throw new InvalidDataException($"Staged {layer} texture source has the wrong layer identity.");
            foreach (var definition in manifest.Definitions.OrderBy(value => value.TargetIndex))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var catalogEntry = catalog.Query(new(Id: definition.Asset.Id)).SingleOrDefault()
                    ?? throw new InvalidDataException($"Staged {layer} asset {definition.Asset.Id} is absent from the catalog.");
                if (catalogEntry.Tags.Contains("texture:placeholder", StringComparer.Ordinal))
                    throw new InvalidDataException(
                        $"{layer} class 0x{definition.ClassId:X4} uses placeholder texture data and cannot be optimized or injected.");
                var path = ForgeProjectPersistence.ResolveRelativePath(root, definition.Resource);
                var canonical = UyaCanonicalAssetCodec.Decode(await File.ReadAllBytesAsync(path, cancellationToken));
                var materialIndex = 0;
                var billboardIndex = 0;
                foreach (var texture in canonical.Textures)
                {
                    var role = texture.Role == 0 ? TextureRole.Material : TextureRole.Billboard;
                    var textureIndex = role == TextureRole.Material ? materialIndex++ : billboardIndex++;
                    inputs.Add(new(
                        definition.Asset.Id.ToString(),
                        Family(layer),
                        definition.ClassId,
                        textureIndex,
                        role,
                        texture.PifBytes,
                        role == TextureRole.Material ? [textureIndex] : []));
                }
            }
        }
        return await Task.Run(
            () => TextureInventoryBuilder.Build(inputs, cancellationToken),
            cancellationToken);
    }

    private static TextureAssetFamily Family(BakeLayerId layer) => layer switch
    {
        BakeLayerId.Ties => TextureAssetFamily.Tie,
        BakeLayerId.Shrubs => TextureAssetFamily.Shrub,
        BakeLayerId.Mobys => TextureAssetFamily.Moby,
        _ => throw new ArgumentOutOfRangeException(nameof(layer)),
    };
}
