namespace Forge.Host.Domain;

public sealed partial class ForgeProjectWorkspace
{
    public ProjectAssetOverride SetFxTextureOverride(
        ProjectAssetReference source,
        ProjectAssetReference replacement)
    {
        RequireFxSourceTexture(source);
        RequireAttachedFxTexture(replacement);
        return SetAssetOverride(source, replacement);
    }

    public ProjectAssetOverride RemoveFxTextureOverride(ProjectAssetReference source)
    {
        RequireFxSourceTexture(source);
        return RemoveAssetOverride(source);
    }

    public ProjectFxTextureAddition AddFxTexture(
        int width,
        int height,
        ProjectAssetReference texture)
    {
        var fx = Content.Fx ?? throw new InvalidOperationException("The project has no FX texture inventory.");
        RequireAttachedFxTexture(texture);
        if (fx.SourceTextures.Count + fx.Additions.Count >= ProjectFxSchema.MaximumTextureCount)
            throw new InvalidDataException("FX additions exceed the texture-count limit.");
        var addition = new ProjectFxTextureAddition(width, height, texture);
        var content = Content with { Fx = fx with { Additions = fx.Additions.Append(addition).ToArray() } };
        ForgeProjectValidation.Validate(RootPath, Manifest, content);
        Content = content;
        return addition;
    }

    public ProjectFxTextureAddition RemoveLastFxTexture(int index)
    {
        var fx = Content.Fx ?? throw new InvalidOperationException("The project has no FX texture inventory.");
        var expected = fx.SourceTextures.Count + fx.Additions.Count - 1;
        if (fx.Additions.Count == 0 || index != expected)
            throw new InvalidOperationException("Only the last appended FX texture can be removed without changing stable indexes.");
        var removed = fx.Additions[^1];
        Content = Content with { Fx = fx with { Additions = fx.Additions.Take(fx.Additions.Count - 1).ToArray() } };
        return removed;
    }

    private void RequireFxSourceTexture(ProjectAssetReference source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Kind != AssetKind.Texture
            || Content.Fx?.SourceTextures.Any(value => value.Texture == source) != true)
            throw new KeyNotFoundException($"Texture {source.Id} is not part of the imported FX inventory.");
    }

    private void RequireAttachedFxTexture(ProjectAssetReference texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        var attached = Content.Assets.SingleOrDefault(asset => asset.Id == texture.Id);
        if (texture.Kind != AssetKind.Texture || attached is null || attached.Kind != AssetKind.Texture
            || attached.CanonicalFormatVersion != ProjectTextureAssetSchema.CanonicalFormatVersion)
            throw new InvalidDataException("FX edits require an attached canonical texture asset.");
    }
}
