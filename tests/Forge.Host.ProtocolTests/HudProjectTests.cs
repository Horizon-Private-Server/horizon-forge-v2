using System.IO.Compression;
using System.Buffers.Binary;
using Forge.Host.Domain;
using Forge.Host.Games.UYA;
using RatchetPs2.Core.Textures;
using RatchetPs2.Core.Textures.Pif;
using RatchetPs2.Core.Textures.Png;
using RatchetPs2.Core.Wad.Models;
using RatchetPs2.Games.UYA.Level;

internal static class HudProjectTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-hud-project-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var imported = ImportedHud();
            Equal(1, imported.State.SourceIcons.Count, "HUD inventory imports native icon mappings");
            Equal(0, imported.State.MinimumAppendBank, "HUD inventory permits append after its last populated bank");
            Equal(imported.Textures.Single().Reference,
                imported.State.SourceIcons.Single().Frames.Single().Texture,
                "HUD inventory binds the exact normalized texture identity");

            var projectPath = Path.Combine(root, "project");
            var sourcePif = Texture(8, 8, 220, 30, 20, checker: false);
            var secondSourcePif = Texture(8, 8, 30, 210, 80, checker: false);
            var additionPif = Texture(16, 8, 240, 200, 30, checker: true);
            var source = new ProjectAssetReference(
                AssetId.Compute(AssetKind.Texture, ProjectTextureAssetSchema.CanonicalFormatVersion, sourcePif),
                AssetKind.Texture);
            var secondSource = new ProjectAssetReference(
                AssetId.Compute(AssetKind.Texture, ProjectTextureAssetSchema.CanonicalFormatVersion, secondSourcePif),
                AssetKind.Texture);
            var hud = new ProjectHudState(
                ProjectHudSchema.CurrentVersion,
                2,
                [
                    new(0, 0x0123, [new(9, 3, 5, 1, 2, 8, 8, source)]),
                    new(1, 0x0124, [new(10, 4, 6, 1, 2, 8, 8, secondSource)]),
                ],
                []);
            var workspace = await ForgeProjectWorkspace.CreateAsync(
                projectPath,
                "HUD project",
                new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
                new("UYA", "NTSC-U", "1.00", 3, "ba9f2b38c7346e7b6e5b8e87717d5893"),
                [],
                null,
                hud);

            await using var runtime = new EditorRuntime(
                hudCommandExecutor: UyaHudProjectService.ExecuteAsync,
                hudMinimumAppendSpriteId: 0xE000,
                hudMaximumAppendSpriteId: 0xEFFF);
            var snapshot = await runtime.OpenAsync(projectPath, TimeSpan.Zero);
            Equal((ushort)0xE000, snapshot.Hud!.MinimumAppendSpriteId,
                "HUD snapshot exposes adapter-owned sprite ID minimum");
            Equal(ProjectHudSchema.PhysicalBankCount, snapshot.Hud.PhysicalBankCount,
                "HUD snapshot exposes the physical bank count");
            Equal((ushort)0xEFFF, snapshot.Hud.MaximumAppendSpriteId,
                "HUD snapshot exposes adapter-owned sprite ID maximum");
            Equal(ProjectHudSchema.MaximumIconCount, snapshot.Hud.MaximumIconCount,
                "HUD snapshot exposes the native icon limit");
            Equal(source, snapshot.Hud!.SourceIcons[0].Frames.Single().SourceTexture,
                "HUD snapshot exposes exact source texture ID");
            Equal(source, snapshot.Hud.SourceIcons[0].Frames.Single().EffectiveTexture,
                "HUD snapshot starts without an override");
            Equal(false, snapshot.Hud.IsDirty, "saved HUD starts clean");

            var (replacementPng, replacementRgba) = PalettePng();
            snapshot = await runtime.ExecuteAsync(new(
                Guid.NewGuid().ToString("D"),
                EditorCommandKind.ReplaceHudTexture,
                [],
                HudEdit: new(SourceAssetId: source.Id, ImageFormat: "png", ImageBytes: replacementPng)));
            var replacement = snapshot.Hud!.SourceIcons[0].Frames.Single().EffectiveTexture!;
            Equal(false, replacement == source, "PNG conversion creates a custom HUD texture");
            Equal(true, snapshot.CanUndo, "HUD replacement participates in history");
            Equal(true, snapshot.Hud.IsDirty, "HUD replacement marks the HUD build layer dirty");

            snapshot = await runtime.SaveAsync();
            Equal(false, snapshot.Hud!.IsDirty, "saving clears HUD build layer dirtiness");
            var convertedWorkspace = await ForgeProjectWorkspace.OpenAsync(projectPath);
            var replacementPath = convertedWorkspace.ResolveAttachedAssetPath(replacement.Id)
                ?? throw new InvalidOperationException("Expected converted HUD texture.");
            var converted = TextureConverter.Decode(
                PifReader.Read(await File.ReadAllBytesAsync(replacementPath)), new() { DoubleAlpha = true });
            Equal(true, replacementRgba.SequenceEqual(converted.PixelData),
                "PNG conversion preserves indexed colors and transparency through PS2 palette ordering");

            snapshot = await runtime.ExecuteAsync(new(
                Guid.NewGuid().ToString("D"), EditorCommandKind.Undo, []));
            Equal(source, snapshot.Hud!.SourceIcons[0].Frames.Single().EffectiveTexture,
                "HUD replacement undo restores source texture");
            Equal(true, snapshot.Hud.IsDirty, "undo away from the saved HUD marks it dirty");
            snapshot = await runtime.ExecuteAsync(new(
                Guid.NewGuid().ToString("D"), EditorCommandKind.Redo, []));
            Equal(replacement, snapshot.Hud!.SourceIcons[0].Frames.Single().EffectiveTexture,
                "HUD replacement redo restores exact override");
            Equal(false, snapshot.Hud.IsDirty, "redo back to the saved HUD clears dirtiness");
            snapshot = await runtime.ExecuteAsync(new(
                Guid.NewGuid().ToString("D"),
                EditorCommandKind.ReplaceHudTexture,
                [],
                HudEdit: new(SourceAssetId: secondSource.Id, ImageFormat: "png", ImageBytes: replacementPng)));
            Equal(replacement, snapshot.Hud!.SourceIcons[1].Frames.Single().EffectiveTexture,
                "identical HUD replacement content deduplicates across source icons");

            snapshot = await runtime.ExecuteAsync(new(
                Guid.NewGuid().ToString("D"),
                EditorCommandKind.AddHudIcon,
                [],
                HudEdit: new(SpriteId: 0xE001, BankIndex: 2, ImageFormat: "pif", ImageBytes: additionPif)));
            var addition = snapshot.Hud!.Additions.Single();
            Equal((ushort)0xE001, addition.SpriteId, "HUD append retains custom sprite ID");
            Equal(2, addition.BankIndex, "HUD append retains validated bank");

            var beforeRejected = snapshot.Hud.Additions.Count;
            await ThrowsAsync<InvalidDataException>(() => runtime.ExecuteAsync(new(
                Guid.NewGuid().ToString("D"),
                EditorCommandKind.AddHudIcon,
                [],
                HudEdit: new(SpriteId: 0xE001, BankIndex: 2, ImageFormat: "pif", ImageBytes: additionPif))));
            snapshot = await runtime.GetSnapshotAsync();
            Equal(beforeRejected, snapshot.Hud!.Additions.Count,
                "duplicate HUD sprite ID does not partially mutate project state");
            await ThrowsAsync<InvalidDataException>(() => runtime.ExecuteAsync(new(
                Guid.NewGuid().ToString("D"),
                EditorCommandKind.AddHudIcon,
                [],
                HudEdit: new(SpriteId: 0xDFFF, BankIndex: 2, ImageFormat: "pif", ImageBytes: additionPif))));
            await ThrowsAsync<InvalidDataException>(() => runtime.ExecuteAsync(new(
                Guid.NewGuid().ToString("D"),
                EditorCommandKind.AddHudIcon,
                [],
                HudEdit: new(SpriteId: 0xE002, BankIndex: 1, ImageFormat: "pif", ImageBytes: additionPif))));

            await runtime.SaveAsync();
            await runtime.CloseAsync();
            workspace = await ForgeProjectWorkspace.OpenAsync(projectPath);
            var savedFingerprint = workspace.CurrentFingerprint;
            Equal(replacement.Id, workspace.ResolveAssetReference(source).Id,
                "HUD override survives save");
            Equal(addition.Texture, workspace.Content.Hud!.Additions.Single().Texture,
                "HUD addition survives save");

            workspace.AddHudIcon(0xE002, 2, addition.Width, addition.Height, addition.Texture);
            var recovery = await workspace.WriteRecoveryAsync()
                ?? throw new InvalidOperationException("Expected HUD recovery snapshot.");
            workspace.RemoveHudIcon(0xE002);
            await workspace.LoadRecoveryAsync(recovery.Id);
            Equal(2, workspace.Content.Hud!.Additions.Count, "HUD additions survive recovery");
            workspace.RemoveHudIcon(0xE002);
            await workspace.SaveAsync();
            Equal(savedFingerprint, workspace.CurrentFingerprint,
                "restoring HUD state restores the deterministic fingerprint");

            var archivePath = Path.Combine(root, "hud-project.zip");
            ZipFile.CreateFromDirectory(projectPath, archivePath, CompressionLevel.Fastest, false);
            var transferredPath = Path.Combine(root, "transferred");
            ZipFile.ExtractToDirectory(archivePath, transferredPath);
            var transferred = await ForgeProjectWorkspace.OpenAsync(transferredPath);
            Equal(savedFingerprint, transferred.CurrentFingerprint,
                "HUD state and custom assets survive ZIP transfer");
            Equal(replacement.Id, transferred.ResolveAssetReference(source).Id,
                "transferred HUD override resolves");

            var brokenPath = Path.Combine(root, "broken");
            ZipFile.ExtractToDirectory(archivePath, brokenPath);
            var broken = await ForgeProjectWorkspace.OpenAsync(brokenPath);
            File.Delete(broken.ResolveAttachedAssetPath(addition.Texture.Id)
                ?? throw new InvalidOperationException("Expected attached HUD texture."));
            await ThrowsAsync<InvalidDataException>(() => ForgeProjectWorkspace.OpenAsync(brokenPath));

            Directory.Delete(
                Path.Combine(transferredPath, ForgeProjectWorkspace.RecoveryDirectoryName),
                recursive: true);
            transferred.RemoveHudTextureOverride(source);
            transferred.RemoveHudTextureOverride(secondSource);
            transferred.RemoveHudIcon(0xE001);
            await transferred.SaveAsync();
            var collected = await transferred.CollectUnreferencedAssetsAsync();
            Equal(true, collected.Contains(replacement.Id), "removed HUD override becomes cleanup candidate");
            Equal(true, collected.Contains(addition.Texture.Id), "removed HUD addition becomes cleanup candidate");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] Texture(int width, int height, byte red, byte green, byte blue, bool checker)
    {
        var palette = new byte[0x400];
        palette[0] = red;
        palette[1] = green;
        palette[2] = blue;
        palette[3] = 128;
        palette[4] = (byte)(255 - red);
        palette[5] = (byte)(255 - green);
        palette[6] = (byte)(255 - blue);
        palette[7] = 128;
        var pixels = new byte[width * height];
        if (checker)
            for (var index = 0; index < pixels.Length; index++) pixels[index] = (byte)(index & 1);
        return PifWriter.Write(PifWriter.CreateIndexed8(width, height, palette, pixels));
    }

    private static (byte[] Png, byte[] Rgba) PalettePng()
    {
        const int width = 32;
        const int height = 32;
        var rgba = new byte[width * height * 4];
        for (var index = 0; index < width * height; index++)
        {
            var offset = index * 4;
            if (index % 7 == 0) continue;
            var color = (byte)(index % 16);
            rgba[offset] = (byte)(color * 13);
            rgba[offset + 1] = (byte)(255 - color * 11);
            rgba[offset + 2] = (byte)(color * 7);
            rgba[offset + 3] = byte.MaxValue;
        }
        return (TextureConverter.EncodePng(new Rgba32Image(width, height, rgba)), rgba);
    }

    private static UyaHudProjectImport ImportedHud()
    {
        var header = new byte[0xd4];
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x00), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x02), 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x04), 0xb4);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x08), 0xc0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x0c), 0xc4);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x10), 0xcc);
        for (var bank = 0; bank < 5; bank++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x14 + bank * 4), 1);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x34 + bank * 4), 1);
        }
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x54), 0x440);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0xb4), 0x0123);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0xb6), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0xb8), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0xbc), 0x0000ffff);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(0xc0), 0);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(0xc2), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0xc4), 0x80000000);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0xcc), 0x80000400);
        header[0xd2] = 3;
        header[0xd3] = 3;

        var bankBytes = new byte[0x440];
        bankBytes[0] = 240;
        bankBytes[1] = 80;
        bankBytes[2] = 30;
        bankBytes[3] = 128;
        var package = new UyaLevelWadPackage(
            new(0, 0, 0, 0, default, default, default, default, [], [], []),
            [
                new PackedFile("hud/header.bin", header, "application/octet-stream"),
                new PackedFile("hud/bank0.bin", bankBytes, "application/octet-stream"),
            ]);
        return UyaHudProjectService.Read(package);
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }

    private static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected {expected}, got {actual}");
    }
}
