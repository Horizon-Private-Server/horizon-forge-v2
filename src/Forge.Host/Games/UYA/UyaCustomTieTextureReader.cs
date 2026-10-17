using Forge.Host.Domain;
using RatchetPs2.Core.LevelAssets;

namespace Forge.Host.Games.UYA;

internal static class UyaCustomTieTextureReader
{
    internal static IReadOnlyList<ProjectAssetReference> OrderMaterialTextures(ProjectCustomModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var materials = model.Materials.OrderBy(value => value.NativeMaterialIndex).ToArray();
        if (!materials.Select(value => value.NativeMaterialIndex)
                .SequenceEqual(Enumerable.Range(0, materials.Length)))
            throw new InvalidDataException("Custom TIE material indices must be contiguous from zero.");
        return materials.Select(value => value.Texture).ToArray();
    }

    internal static async Task<IReadOnlyList<FrontendAssetTexture>> ReadMaterialTexturesAsync(
        ForgeProjectWorkspace workspace,
        ProjectCustomModel model,
        CancellationToken cancellationToken)
    {
        var cache = new Dictionary<ProjectAssetReference, byte[]>();
        var textures = new List<FrontendAssetTexture>(model.Materials.Count);
        foreach (var reference in OrderMaterialTextures(model))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!cache.TryGetValue(reference, out var bytes))
            {
                bytes = await workspace.ReadAttachedAssetVerifiedAsync(
                    reference, ProjectTextureAssetSchema.MaximumCanonicalBytes, cancellationToken);
                cache.Add(reference, bytes);
            }
            textures.Add(new(0, bytes));
        }
        return textures;
    }
}
