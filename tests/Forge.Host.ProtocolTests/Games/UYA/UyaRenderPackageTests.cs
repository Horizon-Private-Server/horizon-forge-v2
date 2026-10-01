using Forge.Host.Games.UYA;
using Forge.Host.Domain;
using System.Buffers.Binary;
using System.Text.Json;
using RatchetPs2.Core.Textures.Pif;
using RatchetPs2.Core.Textures.Png;
using RatchetPs2.Core.Wad.Models;

namespace Forge.Host.ProtocolTests.Games.UYA;

internal static class UyaRenderPackageTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-render-package-{Guid.NewGuid():N}");
        var cache = Path.Combine(root, "cache");
        var projectPath = Path.Combine(root, "project");
        var catalogPath = Path.Combine(root, "catalog");
        var fingerprint = new string('a', 32);
        var skyAssetId = AssetId.Compute(AssetKind.Sky, 0, "sky render source"u8);
        await ForgeProjectWorkspace.CreateAsync(
            projectPath,
            "Render test",
            new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
            new("UYA", "NTSC-U", "1.00", 3, fingerprint, EntityVersion: ProjectSchema.CurrentBaseEntityVersion),
            [
                new(
                    EntityId.New(), "Sky shell 1", "sky", ProjectTransform.Identity,
                    new(skyAssetId, AssetKind.Sky),
                    new("UYA", 3, "level_wad/assets/sky", 0),
                    SkyShell: new(0, 0, new(0, 0, 0), new(0, 0, 0))),
            ]);
        var request = new UyaRenderPackageRequest("", cache, fingerprint, 3, projectPath, catalogPath);
        const string sdkRevision = "test-sdk";
        try
        {
            var package = PackedFilePackageBuilder.Pack([
                new("assets/tfrag/tfrag.gltf", "{}"u8.ToArray(), "model/gltf+json"),
                new("assets/tfrag/tfrag.buffer.bin", [1, 2, 3], "application/octet-stream"),
            ]);
            var sky = PackedFilePackageBuilder.Pack([
                new("assets/skybox/skybox.gltf", "{}"u8.ToArray(), "model/gltf+json"),
                new("assets/skybox/skybox.buffer.bin", [4, 5, 6], "application/octet-stream"),
                new("world/ignored.bin", [7], "application/octet-stream"),
            ]);
            var environment = new UyaRenderEnvironmentResult(57, 65, 50, 40, 50, 40, 10, 175, 255, 0);
            var written = await UyaRenderPackageService.MaterializeAsync(
                request, sdkRevision, package, sky, environment);
            Equal(false, written.CacheHit, "first terrain cache write");
            Equal(true, File.Exists(Path.Combine(written.RootPath, "assets", "tfrag", "tfrag.gltf")), "terrain cache file");
            Equal("assets/tfrag/tfrag.gltf", written.TerrainPaths.Single(), "terrain route");
            Equal("assets/skybox/skybox.gltf", written.SkyPath, "sky route");
            Equal(environment, written.Environment, "environment metadata");
            Equal(false, File.Exists(Path.Combine(written.RootPath, "world", "ignored.bin")), "unrelated common file omitted");

            var cached = await UyaRenderPackageService.PrepareAsync(request, sdkRevision);
            Equal(true, cached.CacheHit, "terrain cache hit without source ISO");
            Equal(written.CacheKey, cached.CacheKey, "stable terrain cache key");
            Equal(0, cached.Assets.Count, "sky entities bypass the model render-asset list");

            var previewRequest = new UyaAssetPreviewRequest(
                cache,
                catalogPath,
                AssetId.Compute(AssetKind.Tie, 2, "preview"u8),
                AssetKind.Tie,
                "UYA");
            var previewPackage = PackedFilePackageBuilder.Pack([
                new("model.gltf", "{}"u8.ToArray(), "model/gltf+json"),
                new("model.buffer.bin", [8, 9, 10], "application/octet-stream"),
            ]);
            var preview = await UyaAssetPreviewService.MaterializeAsync(
                previewRequest, sdkRevision, previewPackage);
            Equal(false, preview.CacheHit, "first asset preview cache write");
            Equal(true, File.Exists(Path.Combine(preview.RootPath, preview.ModelPath)), "asset preview model");

            var previewCached = await UyaAssetPreviewService.MaterializeAsync(
                previewRequest, sdkRevision, previewPackage);
            Equal(true, previewCached.CacheHit, "asset preview cache hit");
            Equal(true, (await UyaAssetPreviewService.PrepareAsync(previewRequest, sdkRevision)).CacheHit,
                "asset preview cache hit does not reopen the catalog");
            Equal(preview.CacheKey, previewCached.CacheKey, "stable asset preview cache key");
            Equal(false,
                preview.CacheKey == UyaAssetPreviewService.CreateCacheKey(previewRequest, "changed-sdk"),
                "SDK revision changes asset preview cache key");

            await File.WriteAllTextAsync(Path.Combine(preview.RootPath, preview.ModelPath), "corrupt");
            var repaired = await UyaAssetPreviewService.MaterializeAsync(
                previewRequest, sdkRevision, previewPackage);
            Equal(false, repaired.CacheHit, "corrupt asset preview cache is replaced");
            Equal("{}", await File.ReadAllTextAsync(Path.Combine(repaired.RootPath, repaired.ModelPath)),
                "repaired asset preview model");

            var palette = new byte[0x400];
            new byte[] { 255, 0, 0, 64, 0, 255, 0, 128 }.CopyTo(palette, 0);
            var pif = PifWriter.Write(PifWriter.CreateIndexed8(2, 2, palette, [0, 1, 1, 0]));
            var catalog = await AssetCatalogStore.OpenAsync(catalogPath);
            var texture = await catalog.PutAsync(
                AssetKind.Texture,
                UyaAssetImportService.TextureCanonicalFormatVersion,
                pif,
                new("test", new("UYA", "NTSC-U", "1.00", "level03", "assets.bin", 0, fingerprint,
                    new(AssetKind.Tie, 1, "material", 0, true))));
            var textureRequest = new UyaAssetPreviewRequest(
                cache, catalogPath, texture.Id, AssetKind.Texture, "UYA", "texture-default");
            var texturePreview = await UyaAssetPreviewService.PrepareAsync(textureRequest, sdkRevision);
            Equal(false, texturePreview.CacheHit, "first texture preview cache write");
            Equal("texture.png", texturePreview.ModelPath, "texture preview route");
            Equal(true, (await File.ReadAllBytesAsync(catalog.ResolveBlobPath(texture.Id)!)).SequenceEqual(pif),
                "texture preview preserves canonical PS2 alpha bytes");
            Equal(true, (await File.ReadAllBytesAsync(Path.Combine(texturePreview.RootPath, texturePreview.ModelPath)))
                .AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0d, 0x0a, 0x1a, 0x0a }),
                "texture preview PNG");
            await using (var textureStream = File.OpenRead(Path.Combine(texturePreview.RootPath, texturePreview.ModelPath)))
            {
                var image = PngTextureMetadataReader.ReadRgba32(textureStream);
                Equal((byte)128, image.PixelData[3], "texture preview doubles partial PS2 alpha");
                Equal(byte.MaxValue, image.PixelData[7], "texture preview clamps opaque PS2 alpha");
            }
            Equal(true, (await UyaAssetPreviewService.PrepareAsync(textureRequest, sdkRevision)).CacheHit,
                "texture preview cache hit");
            var texturePreviewPath = Path.Combine(texturePreview.RootPath, texturePreview.ModelPath);
            var corruptTexture = await File.ReadAllBytesAsync(texturePreviewPath);
            corruptTexture.AsSpan(0, 8).Clear();
            await File.WriteAllBytesAsync(texturePreviewPath, corruptTexture);
            Equal(false, (await UyaAssetPreviewService.PrepareAsync(textureRequest, sdkRevision)).CacheHit,
                "corrupt texture preview cache is replaced");

            var skyBytes = BuildUyaSkyboxFixture(2);
            var skyEntry = await catalog.PutAsync(
                AssetKind.Sky,
                0,
                skyBytes,
                new("test", new("UYA", "NTSC-U", "1.00", "level03", "assets.bin", 0, fingerprint),
                    UyaSkyShellIndexService.Aliases(skyBytes)));
            var firstSkyRequest = new UyaAssetPreviewRequest(
                cache, catalogPath, skyEntry.Id, AssetKind.Sky, "UYA", "sky-default", 0);
            var firstSkyPreview = await UyaAssetPreviewService.PrepareAsync(firstSkyRequest, sdkRevision);
            var secondSkyPreview = await UyaAssetPreviewService.PrepareAsync(
                firstSkyRequest with { ShellIndex = 1 }, sdkRevision);
            Equal("model.gltf", firstSkyPreview.ModelPath, "sky shell preview route");
            Equal(false, firstSkyPreview.CacheKey == secondSkyPreview.CacheKey,
                "sky shell preview cache identity");
            Equal(true, File.Exists(Path.Combine(firstSkyPreview.RootPath, "textures", "tex.0000.png")),
                "first sky shell includes its texture");
            Equal(false, File.Exists(Path.Combine(firstSkyPreview.RootPath, "textures", "tex.0001.png")),
                "first sky shell omits sibling texture");
            Equal(true, File.Exists(Path.Combine(secondSkyPreview.RootPath, "textures", "tex.0001.png")),
                "second sky shell includes its texture");
            Equal(false, File.Exists(Path.Combine(secondSkyPreview.RootPath, "textures", "tex.0000.png")),
                "second sky shell omits sibling texture");
            var fullSkyPreview = await UyaAssetPreviewService.PrepareAsync(
                firstSkyRequest with { ShellIndex = null }, sdkRevision);
            Equal(false, fullSkyPreview.CacheKey == firstSkyPreview.CacheKey,
                "full sky thumbnail source cache identity");
            using (var fullSkyGltf = JsonDocument.Parse(await File.ReadAllBytesAsync(
                Path.Combine(fullSkyPreview.RootPath, fullSkyPreview.ModelPath))))
                Equal(2, fullSkyGltf.RootElement.GetProperty("meshes").GetArrayLength(),
                    "full sky thumbnail source shells");
            Equal(true, File.Exists(Path.Combine(fullSkyPreview.RootPath, "textures", "tex.0000.png"))
                && File.Exists(Path.Combine(fullSkyPreview.RootPath, "textures", "tex.0001.png")),
                "full sky thumbnail source textures");

            var emptyShellSkyBytes = BuildUyaSkyboxFixture(2);
            BinaryPrimitives.WriteInt16LittleEndian(emptyShellSkyBytes.AsSpan(0x30), 0);
            var emptyShellSky = await catalog.PutAsync(
                AssetKind.Sky,
                0,
                emptyShellSkyBytes,
                new("test", new("UYA", "NTSC-U", "1.00", "level03", "assets.bin", 0, fingerprint),
                    UyaSkyShellIndexService.Aliases(emptyShellSkyBytes)));
            var emptyShellPreview = await UyaAssetPreviewService.PrepareAsync(
                firstSkyRequest with { AssetId = emptyShellSky.Id }, sdkRevision);
            using (var emptyShellGltf = JsonDocument.Parse(await File.ReadAllBytesAsync(
                Path.Combine(emptyShellPreview.RootPath, emptyShellPreview.ModelPath))))
                Equal(false, emptyShellGltf.RootElement.TryGetProperty("meshes", out _),
                    "empty sky shell preview is valid meshless glTF");

            var previewTraversal = PackedFilePackageBuilder.Pack([
                new("model.gltf", "{}"u8.ToArray(), "model/gltf+json"),
                new("../escape.bin", [11], "application/octet-stream"),
            ]);
            await ThrowsAsync<InvalidDataException>(() => UyaAssetPreviewService.MaterializeAsync(
                previewRequest with { AssetId = AssetId.Compute(AssetKind.Tie, 2, "traversal"u8) },
                sdkRevision,
                previewTraversal));

            var traversal = PackedFilePackageBuilder.Pack([
                new("../escape.gltf", "{}"u8.ToArray(), "model/gltf+json"),
            ]);
            await ThrowsAsync<InvalidDataException>(() => UyaRenderPackageService.MaterializeAsync(
                request with { Fingerprint = new string('b', 32) }, sdkRevision, traversal));

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var cancelled = request with { Fingerprint = new string('c', 32) };
            await ThrowsAsync<OperationCanceledException>(() => UyaRenderPackageService.MaterializeAsync(
                cancelled, sdkRevision, package, cancellationToken: cancellation.Token));
            Equal(false, Directory.Exists(Path.Combine(root,
                UyaRenderPackageService.CreateCacheKey(cancelled, sdkRevision))), "cancelled cache is absent");

            var previewCancelled = previewRequest with {
                AssetId = AssetId.Compute(AssetKind.Tie, 2, "cancelled"u8),
            };
            await ThrowsAsync<OperationCanceledException>(() => UyaAssetPreviewService.MaterializeAsync(
                previewCancelled, sdkRevision, previewPackage, cancellation.Token));
            Equal(false, Directory.Exists(Path.Combine(cache,
                UyaAssetPreviewService.CreateCacheKey(previewCancelled, sdkRevision))),
                "cancelled asset preview cache is absent");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] BuildUyaSkyboxFixture(int shellCount)
    {
        var dataStart = 0x30 + (shellCount * 0x30);
        var textureDefinitions = dataStart + (shellCount * 0x28);
        var textureData = textureDefinitions + (shellCount * 0x10);
        const int textureStride = 0x401;
        var bytes = new byte[textureData + (shellCount * textureStride)];
        using var stream = new MemoryStream(bytes, writable: true);
        using var writer = new BinaryWriter(stream);
        stream.Position = 6;
        writer.Write(checked((short)shellCount));
        stream.Position = 12;
        writer.Write(checked((short)shellCount));
        stream.Position = 16;
        writer.Write(checked((uint)textureDefinitions));
        writer.Write(checked((uint)textureData));
        stream.Position = 0x20;
        for (var index = 0; index < shellCount; index++) writer.Write(checked((uint)(0x30 + (index * 0x30))));
        for (var index = 0; index < shellCount; index++)
        {
            var shellOffset = 0x30 + (index * 0x30);
            var dataOffset = dataStart + (index * 0x28);
            stream.Position = shellOffset;
            writer.Write((short)1);
            writer.Write((short)1);
            for (var value = 0; value < 6; value++) writer.Write((short)0);
            stream.Position = shellOffset + 0x10;
            writer.Write(0f);
            writer.Write(0f);
            writer.Write(0f);
            writer.Write(1f);
            writer.Write(dataOffset);
            writer.Write((short)3);
            writer.Write((short)1);
            writer.Write((short)0);
            writer.Write((short)24);
            writer.Write((short)36);
            writer.Write((short)40);
            stream.Position = dataOffset;
            foreach (var vertex in new (short X, short Y, short Z)[]
                { (0, 0, 0), (1024, 0, 0), (0, 1024, 0) })
            {
                writer.Write(vertex.X);
                writer.Write(vertex.Y);
                writer.Write(vertex.Z);
                writer.Write((short)0x80);
            }
            stream.Position = dataOffset + 36;
            writer.Write(new byte[] { 0, 1, 2, checked((byte)index) });
            stream.Position = textureDefinitions + (index * 0x10);
            writer.Write(checked((uint)(index * textureStride)));
            writer.Write(checked((uint)((index * textureStride) + 0x400)));
            writer.Write(1);
            writer.Write(1);
            stream.Position = textureData + (index * textureStride);
            writer.Write(new byte[] { 0xff, 0xff, 0xff, 0x80 });
        }
        return bytes;
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
