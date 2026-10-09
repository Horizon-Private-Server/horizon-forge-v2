using Forge.Host.Domain;
using System.Buffers.Binary;
using RatchetPs2.Core.Hud;
using RatchetPs2.Core.IO;
using RatchetPs2.Core.Textures;
using RatchetPs2.Core.Textures.Palettes;
using RatchetPs2.Core.Textures.Pif;
using RatchetPs2.Core.Textures.Png;
using RatchetPs2.Core.Wad;
using RatchetPs2.Core.Wad.Models;
using RatchetPs2.Games.UYA.Hud;
using RatchetPs2.Games.UYA.Level;

namespace Forge.Host.Games.UYA;

internal sealed record UyaHudSourceTexture(
    int FrameIndex,
    ushort SpriteId,
    ProjectAssetReference Reference,
    byte[] CanonicalBytes);

internal sealed record UyaHudProjectImport(
    ProjectHudState State,
    IReadOnlyList<UyaHudSourceTexture> Textures);

internal static class UyaHudProjectService
{
    public static UyaHudProjectImport Read(UyaLevelWadPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var files = package.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        var header = Required(files, "hud/header.bin");
        var banks = Enumerable.Range(0, ProjectHudSchema.PhysicalBankCount)
            .Select(index => files.TryGetValue($"hud/bank{index}.bin", out var file)
                ? Decompress(file.Bytes)
                : [])
            .ToArray();
        var hud = HudBankReader.Read(header, banks);
        var textures = new List<UyaHudSourceTexture>();
        var icons = hud.Icons.Select(icon => new ProjectHudSourceIcon(
            icon.Index,
            icon.IconId,
            Enumerable.Range(icon.FirstFrameIndex, icon.FrameCount)
                .Select(frameIndex => Frame(hud, frameIndex, icon.IconId, textures))
                .ToArray())).ToArray();
        var minimumBank = Math.Max(
            LastPopulatedBank(hud.Header.PaletteCumulativeCounts),
            LastPopulatedBank(hud.Header.TextureCumulativeCounts));
        return new(
            new(ProjectHudSchema.CurrentVersion, minimumBank, icons, []),
            textures);
    }

    public static UyaHudProjectImport? TryRead(UyaLevelWadPackage package) =>
        package.Files.Any(file => file.Path == "hud/header.bin") ? Read(package) : null;

    public static IReadOnlyList<AssetCatalogPut> CreateCatalogPuts(
        UyaHudProjectImport import,
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
                    texture.FrameIndex, fingerprint),
                [$"hud:{texture.SpriteId:X4}:frame:{texture.FrameIndex}"],
                ["vanilla", "game:UYA", $"level:{level:00}", "hud"]))).ToArray();

    public static async Task ExecuteAsync(
        ForgeProjectWorkspace workspace,
        EditorCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(command);
        if (workspace.Manifest.Target.Game != "UYA")
            throw new NotSupportedException($"HUD editing is not supported for {workspace.Manifest.Target.Game}.");
        if (workspace.Content.Hud is null)
            throw new InvalidOperationException("The project has no imported HUD inventory.");

        var state = workspace.CaptureState();
        try
        {
            switch (command.Kind)
            {
                case EditorCommandKind.ReplaceHudTexture:
                {
                    var edit = command.HudEdit!;
                    var source = new ProjectAssetReference(edit.SourceAssetId!.Value, AssetKind.Texture);
                    _ = workspace.Content.Hud.SourceIcons.SelectMany(icon => icon.Frames)
                        .SingleOrDefault(frame => frame.Texture == source)
                        ?? throw new KeyNotFoundException($"Texture {source.Id} is not a valid HUD source texture.");
                    var converted = Indexed8PifImporter.Convert(
                        edit.ImageFormat!, edit.ImageBytes!, 1_024, "HUD", cancellationToken);
                    var attached = await workspace.AttachTextureAssetAsync(
                        converted.PifBytes, source.Id, cancellationToken);
                    workspace.SetHudTextureOverride(source, new(attached.Id, attached.Kind));
                    break;
                }
                case EditorCommandKind.RemoveHudTextureOverride:
                    workspace.RemoveHudTextureOverride(new(
                        command.HudEdit!.SourceAssetId!.Value, AssetKind.Texture));
                    break;
                case EditorCommandKind.AddHudIcon:
                {
                    var edit = command.HudEdit!;
                    var spriteId = edit.SpriteId!.Value;
                    if (!UyaHudSpriteId.IsValidCustomValue(spriteId))
                        throw new InvalidDataException(
                            $"HUD sprite ID {UyaHudSpriteId.Format(spriteId)} is outside the UYA Exxx custom range.");
                    var hud = workspace.Content.Hud;
                    if (edit.BankIndex is not { } bankIndex
                        || bankIndex < hud.MinimumAppendBank
                        || bankIndex >= ProjectHudSchema.PhysicalBankCount)
                        throw new InvalidDataException(
                            $"HUD additions must use a bank from {hud.MinimumAppendBank} through {ProjectHudSchema.PhysicalBankCount - 1}.");
                    if (hud.SourceIcons.Any(icon => icon.SpriteId == spriteId)
                        || hud.Additions.Any(addition => addition.SpriteId == spriteId))
                        throw new InvalidDataException($"HUD sprite ID {UyaHudSpriteId.Format(spriteId)} is already in use.");
                    if (hud.SourceIcons.Count + hud.Additions.Count >= ProjectHudSchema.MaximumIconCount)
                        throw new InvalidDataException("HUD additions exceed the icon mapping limit.");
                    var converted = Indexed8PifImporter.Convert(
                        edit.ImageFormat!, edit.ImageBytes!, 1_024, "HUD", cancellationToken);
                    var attached = await workspace.AttachTextureAssetAsync(
                        converted.PifBytes, null, cancellationToken);
                    workspace.AddHudIcon(
                        spriteId,
                        bankIndex,
                        converted.Width,
                        converted.Height,
                        new(attached.Id, attached.Kind));
                    break;
                }
                case EditorCommandKind.RemoveHudIcon:
                {
                    var spriteId = command.HudEdit!.SpriteId!.Value;
                    if (!UyaHudSpriteId.IsValidCustomValue(spriteId))
                        throw new InvalidDataException(
                            $"HUD sprite ID {UyaHudSpriteId.Format(spriteId)} is outside the UYA Exxx custom range.");
                    workspace.RemoveHudIcon(spriteId);
                    break;
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(command), command.Kind, "Not a HUD command.");
            }
        }
        catch
        {
            workspace.RestoreState(state);
            throw;
        }
    }

    private static ProjectHudSourceFrame Frame(
        HudBankSet hud,
        int frameIndex,
        ushort spriteId,
        ICollection<UyaHudSourceTexture> textures)
    {
        var frame = hud.Frames[frameIndex];
        var palette = hud.Palettes.ElementAtOrDefault(frame.PaletteIndex);
        var texture = hud.Textures.ElementAtOrDefault(frame.TextureIndex);
        var paletteBank = palette?.BankIndex ?? -1;
        var textureBank = texture?.BankIndex ?? -1;
        var width = texture?.Width ?? 0;
        var height = texture?.Height ?? 0;
        if (frame.PaletteIndex < 0 || frame.TextureIndex < 0 || palette is null || texture is null
            || !palette.IsLengthValid || !texture.IsLengthValid)
        {
            return new(
                frame.Index, frame.PaletteIndex, frame.TextureIndex, paletteBank, textureBank,
                width, height, null,
                $"HUD frame {frame.Index} references missing or invalid palette/texture data.");
        }

        try
        {
            var canonical = PifWriter.Write(PifWriter.CreateIndexed8(
                texture.Width,
                texture.Height,
                palette.PaletteBytes,
                texture.PixelBytes));
            var reference = new ProjectAssetReference(
                AssetId.Compute(
                    AssetKind.Texture,
                    ProjectTextureAssetSchema.CanonicalFormatVersion,
                    canonical),
                AssetKind.Texture);
            textures.Add(new(frame.Index, spriteId, reference, canonical));
            return new(
                frame.Index, frame.PaletteIndex, frame.TextureIndex, palette.BankIndex, texture.BankIndex,
                texture.Width, texture.Height, reference);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or OverflowException)
        {
            return new(
                frame.Index, frame.PaletteIndex, frame.TextureIndex, paletteBank, textureBank,
                width, height, null, $"HUD frame {frame.Index} could not be normalized: {exception.Message}");
        }
    }

    private static int LastPopulatedBank(IReadOnlyList<int> cumulativeCounts)
    {
        var final = cumulativeCounts.Count == 0 ? 0 : cumulativeCounts[^1];
        if (final == 0) return 0;
        for (var bank = 0; bank < cumulativeCounts.Count; bank++)
            if (cumulativeCounts[bank] == final) return bank;
        return 0;
    }

    private static byte[] Required(IReadOnlyDictionary<string, PackedFile> files, string path) =>
        files.TryGetValue(path, out var file)
            ? file.Bytes
            : throw new InvalidDataException($"UYA level has no {path} HUD source file.");

    private static byte[] Decompress(byte[] bytes) =>
        BinaryMagic.IsWad(bytes)
            ? WadCompression.Decompress(bytes, new(64 * 1024 * 1024))
            : bytes;

}
