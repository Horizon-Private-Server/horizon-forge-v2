namespace Forge.Host.Domain;

public sealed partial class EditorRuntime
{
    private EditorFxSnapshot? FxSnapshot(ForgeProjectWorkspace workspace)
    {
        if (workspace.Content.Fx is not { } fx) return null;
        var canAuthor = workspace.Manifest.Target.Game == "UYA" && _fxCommandExecutor is not null;
        var hasAppendCapacity = fx.SourceTextures.Count + fx.Additions.Count < ProjectFxSchema.MaximumTextureCount;
        return new(
            true,
            workspace.IsFxDirty,
            canAuthor,
            canAuthor && hasAppendCapacity,
            !canAuthor ? $"FX texture authoring is unavailable for {workspace.Manifest.Target.Game}."
                : !hasAppendCapacity ? "The FX texture-count limit has been reached." : null,
            ProjectFxSchema.MaximumTextureCount,
            fx.SourceTextures.Select(value => new EditorFxSourceTextureSnapshot(
                value.SourceIndex,
                value.Label,
                value.Width,
                value.Height,
                value.PaletteOffset,
                value.PixelOffset,
                value.IsSwizzled,
                value.Texture,
                value.Texture is null ? null : workspace.ResolveAssetReference(value.Texture),
                value.Diagnostic)).ToArray(),
            fx.Additions.ToArray());
    }
}
