using System.Buffers.Binary;
using Forge.Host.Domain;
using RatchetPs2.Games.UYA.Level;

internal static class UyaProjectTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-uya-project-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var catalog = await AssetCatalogStore.OpenAsync(Path.Combine(root, "catalog"));
            var global = await catalog.PutAsync(
                AssetKind.Moby,
                0,
                "moby class 100"u8.ToArray(),
                new(
                    "test-importer",
                    new("UYA", "NTSC-U", "1.00", "level03", "level_wad/assets/asset_wad.bin", 0, new string('a', 32)),
                    ["moby:100", "moby:0x0064"],
                    ["vanilla", "game:UYA", "level:03"]));
            var iso = CreateIso();
            var options = UyaProjectService.GetCreationOptions(new MemoryStream(iso, writable: false));
            Equal(true, options.Levels.SequenceEqual([3]), "base level options");
            Equal(1, options.Warnings.Count, "partial coverage warning");
            var preflight = UyaProjectService.Preflight(new MemoryStream(iso, writable: false), catalog, 3);
            Equal(2, preflight.SourceInstanceCount, "source instance count");
            Equal(1, preflight.RenderableInstanceCount, "renderable instance count");
            Equal(1, preflight.ModelLessInstanceCount, "model-less instance count");
            Equal(0, preflight.MissingAssetInstanceCount, "missing asset instance count");
            Equal(0, preflight.MissingClassCount, "missing class count");
            Equal(1, preflight.Warnings.Count, "preflight warnings");

            var projectPath = Path.Combine(root, "project");
            var request = new UyaProjectCreationRequest(
                "synthetic.iso", catalog.RootPath, projectPath, "Base project", new string('a', 32), "1.00", 3, true);
            var descriptor = await UyaProjectService.CreateValidatedAsync(
                new MemoryStream(iso, writable: false), catalog, request);
            Equal(2, descriptor.EntityCount, "base entity count");
            Equal(0, descriptor.MissingAssetCount, "base missing assets");

            var project = await ForgeProjectWorkspace.OpenAsync(projectPath);
            var entity = project.Content.Entities.Single(candidate => candidate.Asset is not null);
            var modelLess = project.Content.Entities.Single(candidate => candidate.Asset is null);
            Equal(global.Id, entity.Asset!.Id, "global asset reference");
            Equal(1, modelLess.Provenance!.SourceIndex, "model-less source index");
            Equal(3, entity.Provenance!.Level, "entity source level");
            Equal(0, entity.Provenance.SourceIndex, "entity source index");
            Equal(10f, entity.Transform.Position.X, "entity position");
            Near(0.0342708f, entity.Transform.Rotation.X, "ZYX rotation X");
            Near(0.10602051f, entity.Transform.Rotation.Y, "ZYX rotation Y");
            Near(0.14357218f, entity.Transform.Rotation.Z, "ZYX rotation Z");
            Near(0.9833474f, entity.Transform.Rotation.W, "ZYX rotation W");
            var stableId = entity.EntityId;
            project = await ForgeProjectWorkspace.OpenAsync(projectPath);
            Equal(stableId, project.Content.Entities.Single(candidate => candidate.Asset is not null).EntityId, "base entity ID survives reopen");
            project.Rename("Renamed project");
            project.RemoveEntity(stableId);
            await project.SaveAsync();
            var reopened = await ForgeProjectWorkspace.OpenAsync(projectPath);
            Equal("Renamed project", reopened.Manifest.Name, "project rename");
            Equal(1, reopened.Content.Entities.Count, "project-only entity deletion");
            Equal(true, File.Exists(catalog.ResolveBlobPath(global.Id)), "global asset survives deletion");

            var refusedPath = Path.Combine(root, "refused");
            await ThrowsAsync<InvalidDataException>(() => UyaProjectService.CreateValidatedAsync(
                new MemoryStream(iso, writable: false), catalog, request with { ProjectPath = refusedPath, AllowPartial = false }));
            Equal(false, Directory.Exists(refusedPath), "warning confirmation precedes project write");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] CreateIso()
    {
        const int headerSector = 0x500;
        var bytes = new byte[(headerSector + 3) * UyaLevelConstants.SectorSize];
        var info = UyaLevelConstants.RetailLevelInfoTableOffset + 3 * UyaLevelConstants.LevelInfoSize;
        WriteInt32(bytes, info + 0x08, headerSector);
        WriteInt32(bytes, info + 0x0c, 3);

        var wad = bytes.AsSpan(headerSector * UyaLevelConstants.SectorSize);
        WriteInt32(wad, 0x00, UyaLevelConstants.LevelWadHeaderSize);
        WriteInt32(wad, 0x04, headerSector);
        WriteInt32(wad, 0x10, 1);
        WriteInt32(wad, 0x14, 1);
        WriteInt32(wad, 0x20, 2);
        WriteInt32(wad, 0x24, 1);

        var levelData = wad[UyaLevelConstants.SectorSize..];
        WriteByteBlock(levelData, 0x08, 0x100, 0x160);
        WriteByteBlock(levelData, 0x10, 0x240, 1);
        WriteByteBlock(levelData, 0x48, 0x300, 1);
        var assetHeader = levelData[0x100..];
        WriteInt32(assetHeader, 0x18, 1);
        WriteInt32(assetHeader, 0x1c, 0xc0);
        WriteInt32(assetHeader, 0xc0, 0x10);
        WriteInt32(assetHeader, 0xc4, 100);
        assetHeader.Slice(0xd0, 0x10).Fill(byte.MaxValue);

        var gameplay = wad[(2 * UyaLevelConstants.SectorSize)..];
        WriteInt32(gameplay, 0x4c, 0x9c);
        var mobys = gameplay[0x9c..];
        WriteInt32(mobys, 0x00, 2);
        var instance = mobys[0x10..];
        WriteInt32(instance, 0x00, 0x88);
        WriteInt32(instance, 0x10, 7);
        WriteInt32(instance, 0x28, 100);
        WriteSingle(instance, 0x2c, 2);
        WriteSingle(instance, 0x40, 10);
        WriteSingle(instance, 0x44, 20);
        WriteSingle(instance, 0x48, 30);
        WriteSingle(instance, 0x4c, 0.1f);
        WriteSingle(instance, 0x50, 0.2f);
        WriteSingle(instance, 0x54, 0.3f);
        var missingInstance = instance[0x88..];
        WriteInt32(missingInstance, 0x00, 0x88);
        WriteInt32(missingInstance, 0x10, 8);
        WriteInt32(missingInstance, 0x28, 101);
        WriteSingle(missingInstance, 0x2c, 1);
        return bytes;
    }

    private static void WriteInt32(Span<byte> bytes, int offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(bytes[offset..], value);

    private static void WriteSingle(Span<byte> bytes, int offset, float value) =>
        WriteInt32(bytes, offset, BitConverter.SingleToInt32Bits(value));

    private static void WriteByteBlock(Span<byte> bytes, int offset, int blockOffset, int length)
    {
        WriteInt32(bytes, offset, blockOffset);
        WriteInt32(bytes, offset + 4, length);
    }

    private static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected {expected}, got {actual}");
    }

    private static void Near(float expected, float actual, string context)
    {
        if (MathF.Abs(expected - actual) > 0.000001f)
            throw new InvalidOperationException($"{context}: expected {expected}, got {actual}");
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }
}
