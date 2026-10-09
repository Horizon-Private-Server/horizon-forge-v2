namespace Forge.Host.Domain;

public sealed partial class ForgeProjectWorkspace
{
    public ProjectAssetOverride SetHudTextureOverride(
        ProjectAssetReference source,
        ProjectAssetReference replacement)
    {
        RequireHudSourceTexture(source);
        RequireAttachedHudTexture(replacement);
        return SetAssetOverride(source, replacement);
    }

    public ProjectAssetOverride RemoveHudTextureOverride(ProjectAssetReference source)
    {
        RequireHudSourceTexture(source);
        return RemoveAssetOverride(source);
    }

    public ProjectHudIconAddition AddHudIcon(
        ushort spriteId,
        int bankIndex,
        int width,
        int height,
        ProjectAssetReference texture)
    {
        var hud = Content.Hud ?? throw new InvalidOperationException("The project has no HUD inventory.");
        RequireAttachedHudTexture(texture);
        var addition = new ProjectHudIconAddition(spriteId, bankIndex, width, height, texture);
        var content = Content with
        {
            Hud = hud with
            {
                Additions = hud.Additions.Append(addition)
                    .OrderBy(value => value.BankIndex)
                    .ThenBy(value => value.SpriteId)
                    .ToArray(),
            },
        };
        ForgeProjectValidation.Validate(RootPath, Manifest, content);
        Content = content;
        return addition;
    }

    public ProjectHudIconAddition RemoveHudIcon(ushort spriteId)
    {
        var hud = Content.Hud ?? throw new InvalidOperationException("The project has no HUD inventory.");
        var addition = hud.Additions.SingleOrDefault(value => value.SpriteId == spriteId)
            ?? throw new KeyNotFoundException($"HUD sprite ID {spriteId:X4} is not a project addition.");
        Content = Content with
        {
            Hud = hud with { Additions = hud.Additions.Where(value => value != addition).ToArray() },
        };
        return addition;
    }

    private void RequireHudSourceTexture(ProjectAssetReference source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Kind != AssetKind.Texture
            || Content.Hud?.SourceIcons.SelectMany(icon => icon.Frames)
                .Any(frame => frame.Texture == source) != true)
            throw new KeyNotFoundException($"Texture {source.Id} is not part of the imported HUD inventory.");
    }

    private void RequireAttachedHudTexture(ProjectAssetReference texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        var attached = Content.Assets.SingleOrDefault(asset => asset.Id == texture.Id);
        if (texture.Kind != AssetKind.Texture || attached is null || attached.Kind != AssetKind.Texture
            || attached.CanonicalFormatVersion != ProjectTextureAssetSchema.CanonicalFormatVersion)
            throw new InvalidDataException("HUD edits require an attached canonical texture asset.");
    }
}
