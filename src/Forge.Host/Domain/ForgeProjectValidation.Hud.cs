namespace Forge.Host.Domain;

internal static partial class ForgeProjectValidation
{
    private static void ValidateHud(
        string rootPath,
        ProjectHudState hud,
        IReadOnlyList<ProjectAttachedAsset> assets)
    {
        if (hud.SchemaVersion != ProjectHudSchema.CurrentVersion)
            throw new InvalidDataException($"Unsupported HUD project schema {hud.SchemaVersion}.");
        if (hud.MinimumAppendBank is < 0 or >= ProjectHudSchema.PhysicalBankCount)
            throw new InvalidDataException("Project HUD minimum append bank is invalid.");
        if (hud.SourceIcons is null || hud.Additions is null
            || hud.SourceIcons.Any(icon => icon is null)
            || hud.Additions.Any(addition => addition is null))
            throw new InvalidDataException("Project HUD lists cannot contain null entries.");
        if (hud.SourceIcons.Count + hud.Additions.Count > ProjectHudSchema.MaximumIconCount)
            throw new InvalidDataException("Project HUD exceeds the icon mapping limit.");
        if (!hud.SourceIcons.Select(icon => icon.SourceIconIndex)
            .SequenceEqual(Enumerable.Range(0, hud.SourceIcons.Count)))
            throw new InvalidDataException("Project HUD source icons must retain contiguous source order.");

        var spriteIds = new HashSet<ushort>();
        var sourceFrames = new Dictionary<int, ProjectHudSourceFrame>();
        foreach (var icon in hud.SourceIcons)
        {
            if (icon.SourceIconIndex < 0 || icon.Frames is null || icon.Frames.Count > ushort.MaxValue
                || !spriteIds.Add(icon.SpriteId))
                throw new InvalidDataException("Project HUD source icon metadata is invalid.");
            if (!icon.Frames.Select(frame => frame.SourceFrameIndex)
                .SequenceEqual(icon.Frames.Select(frame => frame.SourceFrameIndex).Order()))
                throw new InvalidDataException($"Project HUD icon {icon.SourceIconIndex} frames are not in source order.");
            foreach (var frame in icon.Frames)
            {
                if (frame.SourceFrameIndex is < 0 or > ushort.MaxValue)
                    throw new InvalidDataException("Project HUD source frame index is invalid.");
                if (frame.Texture is null)
                {
                    if (string.IsNullOrWhiteSpace(frame.Diagnostic) || frame.Diagnostic.Length > 1_024)
                        throw new InvalidDataException(
                            $"HUD source frame {frame.SourceFrameIndex} must explain its missing texture.");
                }
                else
                {
                    if (frame.SourcePaletteIndex is < 0 or > short.MaxValue
                        || frame.SourceTextureIndex is < 0 or > short.MaxValue)
                        throw new InvalidDataException("Project HUD source texture indexes are invalid.");
                    if (frame.Diagnostic is not null)
                        throw new InvalidDataException(
                            $"HUD source frame {frame.SourceFrameIndex} cannot be valid and diagnostic at the same time.");
                    ValidateHudTexture(
                        frame.Texture, frame.Width, frame.Height, frame.PaletteBankIndex, frame.TextureBankIndex,
                        $"HUD source frame {frame.SourceFrameIndex}");
                }
                if (sourceFrames.TryGetValue(frame.SourceFrameIndex, out var existing) && existing != frame)
                    throw new InvalidDataException($"Project HUD source frame {frame.SourceFrameIndex} is inconsistent.");
                sourceFrames[frame.SourceFrameIndex] = frame;
            }
        }

        if (!hud.Additions.Select(addition => (addition.BankIndex, addition.SpriteId))
            .SequenceEqual(hud.Additions.Select(addition => (addition.BankIndex, addition.SpriteId)).Order()))
            throw new InvalidDataException("Project HUD additions must use deterministic bank and sprite-ID order.");
        foreach (var addition in hud.Additions)
        {
            if (!spriteIds.Add(addition.SpriteId))
                throw new InvalidDataException($"Project HUD sprite ID {addition.SpriteId:X4} is duplicated.");
            if (addition.BankIndex < hud.MinimumAppendBank)
                throw new InvalidDataException(
                    $"HUD addition {addition.SpriteId:X4} cannot use bank {addition.BankIndex} before bank {hud.MinimumAppendBank}.");
            ValidateHudTexture(
                addition.Texture, addition.Width, addition.Height, addition.BankIndex, addition.BankIndex,
                $"HUD addition {addition.SpriteId:X4}");
            var asset = assets.SingleOrDefault(value => value.Id == addition.Texture.Id);
            if (asset is null || asset.Kind != AssetKind.Texture
                || asset.CanonicalFormatVersion != ProjectTextureAssetSchema.CanonicalFormatVersion)
                throw new InvalidDataException($"HUD addition {addition.SpriteId:X4} has no compatible attached texture.");
            var value = asset.Id.ToString();
            if (!File.Exists(Path.Combine(rootPath, "assets", value[..2], $"{value}.blob")))
                throw new InvalidDataException($"HUD addition {addition.SpriteId:X4} texture blob is missing.");
        }
    }

    private static void ValidateHudTexture(
        ProjectAssetReference texture,
        int width,
        int height,
        int paletteBankIndex,
        int textureBankIndex,
        string label)
    {
        if (texture is null || texture.Kind != AssetKind.Texture
            || texture.Id.ToString().Length != AssetId.TextLength
            || paletteBankIndex is < 0 or >= ProjectHudSchema.PhysicalBankCount
            || textureBankIndex is < 0 or >= ProjectHudSchema.PhysicalBankCount
            || width <= 0 || height <= 0
            || (width & (width - 1)) != 0 || (height & (height - 1)) != 0)
            throw new InvalidDataException($"{label} texture metadata is invalid.");
    }
}
