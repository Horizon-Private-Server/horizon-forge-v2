namespace Forge.Host.Domain;

internal static partial class ForgeProjectValidation
{
    private static void ValidateFx(
        string rootPath,
        ProjectFxState fx,
        IReadOnlyList<ProjectAttachedAsset> assets)
    {
        if (fx.SchemaVersion != ProjectFxSchema.CurrentVersion)
            throw new InvalidDataException($"Unsupported FX project schema {fx.SchemaVersion}.");
        if (fx.SourceTextures is null || fx.Additions is null
            || fx.SourceTextures.Any(value => value is null)
            || fx.Additions.Any(value => value is null))
            throw new InvalidDataException("Project FX lists cannot contain null entries.");
        if (fx.SourceTextures.Count + fx.Additions.Count > ProjectFxSchema.MaximumTextureCount)
            throw new InvalidDataException("Project FX inventory exceeds the texture-count limit.");
        if (!fx.SourceTextures.Select(value => value.SourceIndex)
            .SequenceEqual(Enumerable.Range(0, fx.SourceTextures.Count)))
            throw new InvalidDataException("Project FX source textures must retain contiguous source order.");

        foreach (var source in fx.SourceTextures)
        {
            if (string.IsNullOrWhiteSpace(source.Label) || source.Label.Length > 256)
                throw new InvalidDataException($"FX source texture {source.SourceIndex} label is invalid.");
            if (source.Texture is null)
            {
                if (string.IsNullOrWhiteSpace(source.Diagnostic) || source.Diagnostic.Length > 1_024)
                    throw new InvalidDataException(
                        $"FX source texture {source.SourceIndex} must explain its missing texture.");
                continue;
            }
            if (source.Diagnostic is not null || source.PaletteOffset < 0 || source.PixelOffset < 0)
                throw new InvalidDataException($"FX source texture {source.SourceIndex} metadata is invalid.");
            ValidateFxTexture(source.Texture, source.Width, source.Height, $"FX source texture {source.SourceIndex}");
        }

        for (var index = 0; index < fx.Additions.Count; index++)
        {
            var addition = fx.Additions[index];
            ValidateFxTexture(addition.Texture, addition.Width, addition.Height, $"FX addition {index}");
            var asset = assets.SingleOrDefault(value => value.Id == addition.Texture.Id);
            if (asset is null || asset.Kind != AssetKind.Texture
                || asset.CanonicalFormatVersion != ProjectTextureAssetSchema.CanonicalFormatVersion)
                throw new InvalidDataException($"FX addition {index} has no compatible attached texture.");
            var value = asset.Id.ToString();
            if (!File.Exists(Path.Combine(rootPath, "assets", value[..2], $"{value}.blob")))
                throw new InvalidDataException($"FX addition {index} texture blob is missing.");
        }
    }

    private static void ValidateFxTexture(
        ProjectAssetReference texture,
        int width,
        int height,
        string label)
    {
        if (texture.Kind != AssetKind.Texture
            || texture.Id.ToString().Length != AssetId.TextLength
            || width is <= 0 or > 4_096
            || height is <= 0 or > 4_096
            || (width & (width - 1)) != 0
            || (height & (height - 1)) != 0)
            throw new InvalidDataException($"{label} texture metadata is invalid.");
    }
}
