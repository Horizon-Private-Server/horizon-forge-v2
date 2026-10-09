namespace Forge.Host.Domain;

public sealed partial class EditorRuntime
{
    private EditorHudSnapshot? HudSnapshot(ForgeProjectWorkspace workspace)
    {
        if (workspace.Content.Hud is not { } hud) return null;
        var canAuthor = workspace.Manifest.Target.Game == "UYA" && _hudCommandExecutor is not null;
        var hasAppendCapacity = hud.SourceIcons.Count + hud.Additions.Count < ProjectHudSchema.MaximumIconCount;
        return new(
            true,
            workspace.IsHudDirty,
            canAuthor,
            canAuthor && hasAppendCapacity,
            !canAuthor ? $"HUD authoring is unavailable for {workspace.Manifest.Target.Game}."
                : !hasAppendCapacity ? "The HUD icon mapping limit has been reached." : null,
            ProjectHudSchema.PhysicalBankCount,
            hud.MinimumAppendBank,
            _hudMinimumAppendSpriteId,
            _hudMaximumAppendSpriteId,
            ProjectHudSchema.MaximumIconCount,
            hud.SourceIcons.Select(icon => new EditorHudIconSnapshot(
                icon.SourceIconIndex,
                icon.SpriteId,
                icon.Frames.Select(frame => new EditorHudFrameSnapshot(
                    frame.SourceFrameIndex,
                    frame.SourcePaletteIndex,
                    frame.SourceTextureIndex,
                    frame.PaletteBankIndex,
                    frame.TextureBankIndex,
                    frame.Width,
                    frame.Height,
                    frame.Texture,
                    frame.Texture is null ? null : workspace.ResolveAssetReference(frame.Texture),
                    frame.Diagnostic)).ToArray())).ToArray(),
            hud.Additions.ToArray());
    }
}
