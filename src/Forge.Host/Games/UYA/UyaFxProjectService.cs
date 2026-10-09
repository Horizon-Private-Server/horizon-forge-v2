using Forge.Host.Domain;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.Textures;
using RatchetPs2.Core.Wad.Models;
using RatchetPs2.Games.UYA.Level;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.UYA;

internal sealed record UyaFxSourceTexture(
    int Index,
    ProjectAssetReference Reference,
    byte[] CanonicalBytes);

internal sealed record UyaFxProjectImport(
    ProjectFxState State,
    IReadOnlyList<UyaFxSourceTexture> Textures);

internal static class UyaFxProjectService
{
    public static UyaFxProjectImport Read(UyaLevelWadPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var files = package.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        var inventory = FxTextureCatalog.Read(
            GameId.UYA,
            Required(files, "assets/asset_header.bin"),
            Required(files, "assets/asset_wad.bin"));
        var textures = new List<UyaFxSourceTexture>();
        var source = inventory.Entries.Select(entry =>
        {
            ProjectAssetReference? reference = null;
            if (entry.IsValid)
            {
                reference = new(
                    AssetId.Compute(
                        AssetKind.Texture,
                        ProjectTextureAssetSchema.CanonicalFormatVersion,
                        entry.CanonicalTextureBytes),
                    AssetKind.Texture);
                textures.Add(new(entry.Index, reference, entry.CanonicalTextureBytes));
            }
            return new ProjectFxSourceTexture(
                entry.Index,
                entry.Label,
                entry.Width,
                entry.Height,
                entry.PaletteOffset,
                entry.PixelOffset,
                entry.IsSwizzled,
                reference,
                entry.Diagnostic);
        }).ToArray();
        return new(new(ProjectFxSchema.CurrentVersion, source, []), textures);
    }

    public static UyaFxProjectImport? TryRead(UyaLevelWadPackage package) =>
        package.Files.Any(file => file.Path == "assets/asset_header.bin")
        && package.Files.Any(file => file.Path == "assets/asset_wad.bin")
            ? Read(package)
            : null;

    public static IReadOnlyList<AssetCatalogPut> CreateCatalogPuts(
        UyaFxProjectImport import,
        int level,
        string revision,
        string fingerprint,
        string importerVersion) => import.Textures.Select(texture => new AssetCatalogPut(
            AssetKind.Texture,
            ProjectTextureAssetSchema.CanonicalFormatVersion,
            texture.CanonicalBytes,
            new(
                importerVersion,
                new(
                    "UYA", "NTSC-U", revision, $"level{level:00}", "level_wad/level_data.wad",
                    texture.Index, fingerprint),
                [$"fx:{texture.Index}"],
                ["vanilla", "game:UYA", $"level:{level:00}", "fx"]))).ToArray();

    public static async Task ExecuteAsync(
        ForgeProjectWorkspace workspace,
        EditorCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(command);
        if (workspace.Manifest.Target.Game != "UYA")
            throw new NotSupportedException($"FX texture editing is not supported for {workspace.Manifest.Target.Game}.");
        if (workspace.Content.Fx is null)
            throw new InvalidOperationException("The project has no imported FX texture inventory.");

        var state = workspace.CaptureState();
        try
        {
            switch (command.Kind)
            {
                case EditorCommandKind.ReplaceFxTexture:
                {
                    var edit = command.FxEdit!;
                    var source = new ProjectAssetReference(edit.SourceAssetId!.Value, AssetKind.Texture);
                    if (!workspace.Content.Fx.SourceTextures.Any(value => value.Texture == source))
                        throw new KeyNotFoundException($"Texture {source.Id} is not a valid FX source texture.");
                    var converted = Indexed8PifImporter.Convert(
                        edit.ImageFormat!, edit.ImageBytes!, 4_096, "FX", cancellationToken);
                    var attached = await workspace.AttachTextureAssetAsync(
                        converted.PifBytes, source.Id, cancellationToken);
                    workspace.SetFxTextureOverride(source, new(attached.Id, attached.Kind));
                    break;
                }
                case EditorCommandKind.RemoveFxTextureOverride:
                    workspace.RemoveFxTextureOverride(new(
                        command.FxEdit!.SourceAssetId!.Value, AssetKind.Texture));
                    break;
                case EditorCommandKind.AddFxTexture:
                {
                    var edit = command.FxEdit!;
                    var converted = Indexed8PifImporter.Convert(
                        edit.ImageFormat!, edit.ImageBytes!, 4_096, "FX", cancellationToken);
                    var attached = await workspace.AttachTextureAssetAsync(
                        converted.PifBytes, null, cancellationToken);
                    workspace.AddFxTexture(
                        converted.Width, converted.Height, new(attached.Id, attached.Kind));
                    break;
                }
                case EditorCommandKind.RemoveFxTexture:
                    workspace.RemoveLastFxTexture(command.FxEdit!.Index!.Value);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(command), command.Kind, "Not an FX texture command.");
            }
        }
        catch
        {
            workspace.RestoreState(state);
            throw;
        }
    }

    private static byte[] Required(IReadOnlyDictionary<string, PackedFile> files, string path) =>
        files.TryGetValue(path, out var file)
            ? file.Bytes
            : throw new InvalidDataException($"UYA level has no {path} FX source file.");
}
