using System.Buffers.Binary;
using System.IO.Compression;
using Forge.Host.Domain;
using Forge.Host.Games.UYA;
using RatchetPs2.Core.Textures.Pif;
using RatchetPs2.Core.Wad.Models;
using RatchetPs2.Core.Games;
using RatchetPs2.Games.UYA.Level;
using RatchetPs2.Sdk;

internal static class FxProjectTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-fx-project-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var fixture = ImportedFx();
            var imported = fixture.Import;
            Equal(2, imported.State.SourceTextures.Count, "FX inventory imports ordered native definitions");
            Equal("FX_LAME_SHADOW", imported.State.SourceTextures[0].Label,
                "FX inventory uses the game-owned label catalog");
            Equal(imported.Textures[0].Reference, imported.State.SourceTextures[0].Texture,
                "FX inventory binds exact normalized texture identity");

            var projectPath = Path.Combine(root, "project");
            var fx = imported.State;
            var workspace = await ForgeProjectWorkspace.CreateAsync(
                projectPath,
                "FX project",
                new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
                new("UYA", "NTSC-U", "1.00", 3, "ba9f2b38c7346e7b6e5b8e87717d5893"),
                [],
                null,
                null,
                fx);
            await using var runtime = new EditorRuntime(fxCommandExecutor: UyaFxProjectService.ExecuteAsync);
            var snapshot = await runtime.OpenAsync(projectPath, TimeSpan.Zero);
            Equal(false, snapshot.Fx!.IsDirty, "saved FX inventory starts clean");
            Equal(true, snapshot.Fx.CanReplace && snapshot.Fx.CanAppend,
                "UYA FX snapshot exposes writer capabilities");

            var source = snapshot.Fx.SourceTextures[0].SourceTexture!;
            var replacementPif = Texture(8, 4, 0x42);
            snapshot = await runtime.ExecuteAsync(new(
                Guid.NewGuid().ToString("D"),
                EditorCommandKind.ReplaceFxTexture,
                [],
                FxEdit: new(SourceAssetId: source.Id, ImageFormat: "pif", ImageBytes: replacementPif)));
            var replacement = snapshot.Fx!.SourceTextures[0].EffectiveTexture!;
            Equal(false, replacement == source, "FX replacement creates a project-local texture");
            Equal(true, snapshot.Fx.IsDirty, "FX replacement marks only the FX project state dirty");
            Equal(true, File.Exists(AttachedPath(projectPath, replacement.Id)),
                "FX replacement bytes are copied into the project");

            var additionPif = Texture(4, 8, 0x84);
            snapshot = await runtime.ExecuteAsync(new(
                Guid.NewGuid().ToString("D"),
                EditorCommandKind.AddFxTexture,
                [],
                FxEdit: new(ImageFormat: "pif", ImageBytes: additionPif)));
            Equal(1, snapshot.Fx!.Additions.Count, "FX append adds one trailing project texture");
            var additionIndex = snapshot.Fx.SourceTextures.Count;
            Equal(true, File.Exists(AttachedPath(projectPath, snapshot.Fx.Additions[0].Texture.Id)),
                "FX addition bytes are copied into the project");

            snapshot = await runtime.ExecuteAsync(new(
                Guid.NewGuid().ToString("D"),
                EditorCommandKind.AddFxTexture,
                [],
                FxEdit: new(ImageFormat: "pif", ImageBytes: replacementPif)));
            await ThrowsAsync<InvalidOperationException>(() => runtime.ExecuteAsync(new(
                Guid.NewGuid().ToString("D"),
                EditorCommandKind.RemoveFxTexture,
                [],
                FxEdit: new(Index: additionIndex))));
            Equal(2, (await runtime.GetSnapshotAsync()).Fx!.Additions.Count,
                "rejecting non-trailing FX removal preserves stable indexes and project state");
            snapshot = await runtime.ExecuteAsync(new(
                Guid.NewGuid().ToString("D"),
                EditorCommandKind.RemoveFxTexture,
                [],
                FxEdit: new(Index: additionIndex + 1)));
            Equal(1, snapshot.Fx!.Additions.Count, "last appended FX texture can be removed safely");

            snapshot = await runtime.SaveAsync();
            Equal(false, snapshot.Fx!.IsDirty, "saving clears FX build-layer dirtiness");
            await runtime.CloseAsync();
            workspace = await ForgeProjectWorkspace.OpenAsync(projectPath);
            var fingerprint = workspace.CurrentFingerprint;
            Equal(replacement, workspace.ResolveAssetReference(source), "FX override survives save");

            var staging = await BakeStagingStore.OpenAsync(projectPath);
            var staged = await UyaFxBakeService.StageAsync(
                projectPath,
                staging,
                new(BakeLayerId.Fx, BakeLayerState.Dirty, new string('a', 64), new string('b', 64), [], []),
                CancellationToken.None);
            var stagedRoot = ForgeProjectPersistence.ResolveRelativePath(staging.RootPath, staged.RelativePath);
            var composition = await UyaFxBakeService.ComposeAsync(
                stagedRoot, fixture.Header, fixture.Asset, CancellationToken.None)
                ?? throw new InvalidOperationException("Expected staged FX composition.");
            var composed = FxTextureCatalog.Read(
                GameId.UYA, composition.HeaderBytes, composition.AssetBytes);
            Equal(3, composed.Entries.Count, "FX bake preserves two source indexes and appends one trailing index");
            Equal(8, composed.Entries[0].Width, "FX bake applies exact override to the first shared source identity");
            Equal(8, composed.Entries[1].Width, "FX bake applies exact override to every shared source identity");
            Equal(4, composed.Entries[2].Width, "FX bake appends the project texture deterministically");
            var sourceModelOffset = BinaryPrimitives.ReadInt32LittleEndian(fixture.Header.AsSpan(0xe0));
            var outputModelOffset = BinaryPrimitives.ReadInt32LittleEndian(composition.HeaderBytes.AsSpan(0xe0));
            Equal(true, outputModelOffset > sourceModelOffset
                && composition.AssetBytes.AsSpan(outputModelOffset, 0x20)
                    .SequenceEqual(fixture.Asset.AsSpan(sourceModelOffset, 0x20)),
                "FX bake keeps expanded FX data before the relocated model heap");

            var archivePath = Path.Combine(root, "fx-project.zip");
            ZipFile.CreateFromDirectory(projectPath, archivePath, CompressionLevel.Fastest, false);
            var transferredPath = Path.Combine(root, "transferred");
            ZipFile.ExtractToDirectory(archivePath, transferredPath);
            var transferred = await ForgeProjectWorkspace.OpenAsync(transferredPath);
            Equal(fingerprint, transferred.CurrentFingerprint, "FX state and assets survive ZIP transfer");
            Equal(replacement, transferred.ResolveAssetReference(source), "transferred FX override resolves");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static FxFixture ImportedFx()
    {
        var header = new byte[0x100];
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x18), 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x1c), 0xe0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x58), 2);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x5c), 0xc0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x68), 0x100);
        WriteDefinition(header.AsSpan(0xc0, 0x10), 0, 0x400, 4, 4);
        WriteDefinition(header.AsSpan(0xd0, 0x10), 0x500, 0x900, 4, 4);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0xe0), 0xb00);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0xe4), 1);
        var asset = new byte[0xb20];
        for (var index = 0; index < asset.Length; index++) asset[index] = unchecked((byte)index);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x7c), asset.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x8c), asset.Length);
        var import = UyaFxProjectService.Read(new(
            new(0, 0, 0, 0, default, default, default, default, [], [], []),
            [
                new PackedFile("assets/asset_header.bin", header, "application/octet-stream"),
                new PackedFile("assets/asset_wad.bin", asset, "application/octet-stream"),
            ]));
        return new(import, header, asset);
    }

    private static byte[] Texture(int width, int height, byte marker)
    {
        var palette = Enumerable.Range(0, 0x400).Select(index => unchecked((byte)(marker + index))).ToArray();
        var pixels = Enumerable.Range(0, width * height).Select(index => unchecked((byte)(marker ^ index))).ToArray();
        return PifWriter.Write(PifWriter.CreateIndexed8(width, height, palette, pixels));
    }

    private static void WriteDefinition(
        Span<byte> destination,
        int paletteOffset,
        int pixelOffset,
        int width,
        int height)
    {
        BinaryPrimitives.WriteInt32LittleEndian(destination, paletteOffset);
        BinaryPrimitives.WriteInt32LittleEndian(destination[4..], pixelOffset);
        BinaryPrimitives.WriteInt32LittleEndian(destination[8..], width);
        BinaryPrimitives.WriteInt32LittleEndian(destination[12..], height);
    }

    private static string AttachedPath(string projectPath, AssetId id)
    {
        var value = id.ToString();
        return Path.Combine(projectPath, "assets", value[..2], $"{value}.blob");
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

    private sealed record FxFixture(UyaFxProjectImport Import, byte[] Header, byte[] Asset);
}
