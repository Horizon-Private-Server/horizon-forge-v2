using System.Security.Cryptography;
using Forge.Host.Domain;

internal static class ForgeProjectTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-project-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var catalog = await AssetCatalogStore.OpenAsync(Path.Combine(root, "catalog"));
            var globalBytes = "global moby"u8.ToArray();
            var global = await catalog.PutAsync(
                AssetKind.Moby,
                canonicalFormatVersion: 0,
                globalBytes,
                new(
                    "test-importer",
                    new("UYA", "NTSC-U", "1.00", "level03", "level_wad/assets/asset_wad.bin", 7, UyaIsoService.SupportedMd5)));
            var globalBlob = catalog.ResolveBlobPath(global.Id)!;
            var globalHash = SHA256.HashData(await File.ReadAllBytesAsync(globalBlob));
            var firstId = EntityId.New();
            var secondId = EntityId.New();
            var entities = new[]
            {
                Entity(firstId, "First", global.Id),
                Entity(secondId, "Second", global.Id),
            };
            var target = new ProjectTargetProfile("UYA", "NTSC-U", "1.00", "uya-ntsc-u");
            var baseLevel = new ProjectBaseLevel("UYA", "NTSC-U", "1.00", 3, UyaIsoService.SupportedMd5);
            var originalPath = Path.Combine(root, "project-a");
            var otherPath = Path.Combine(root, "project-b");
            var project = await ForgeProjectWorkspace.CreateAsync(originalPath, "Portable project", target, baseLevel, entities);
            var otherProject = await ForgeProjectWorkspace.CreateAsync(otherPath, "Other project", target, baseLevel, entities);

            project.UpdateTransform(firstId, ProjectTransform.Identity with { Position = new(1, 2, 3) });
            Equal(0, project.Content.Assets.Count, "transform does not copy assets");
            Equal(global.Id, project.Content.Entities[0].Asset!.Id, "transform retains global reference");
            var currentGlobalHash = SHA256.HashData(await File.ReadAllBytesAsync(globalBlob));
            Equal(true, globalHash.SequenceEqual(currentGlobalHash), "global blob unchanged");
            await project.SaveAsync();

            var manifestBefore = await File.ReadAllBytesAsync(Path.Combine(originalPath, ForgeProjectWorkspace.ManifestFileName));
            var contentBefore = await File.ReadAllBytesAsync(Path.Combine(originalPath, ForgeProjectWorkspace.DefaultContentPath));
            Equal(false, System.Text.Encoding.UTF8.GetString(manifestBefore).Contains(root, StringComparison.Ordinal), "manifest contains no machine path");
            var movedPath = Path.Combine(root, "moved-project-a");
            Directory.Move(originalPath, movedPath);
            project = await ForgeProjectWorkspace.OpenAsync(movedPath);
            await project.SaveAsync();
            var manifestAfter = await File.ReadAllBytesAsync(Path.Combine(movedPath, ForgeProjectWorkspace.ManifestFileName));
            var contentAfter = await File.ReadAllBytesAsync(Path.Combine(movedPath, ForgeProjectWorkspace.DefaultContentPath));
            Equal(true, manifestBefore.SequenceEqual(manifestAfter), "manifest deterministic round trip");
            Equal(true, contentBefore.SequenceEqual(contentAfter), "content deterministic round trip");
            Equal(firstId, project.Content.Entities[0].EntityId, "entity ID survives move and reopen");

            var contentPath = Path.Combine(movedPath, ForgeProjectWorkspace.DefaultContentPath);
            var futureContent = "{\"schemaVersion\":1,\"futureData\":true}"u8.ToArray();
            await File.WriteAllBytesAsync(contentPath, futureContent);
            await ThrowsAsync<UnsupportedProjectSchemaException>(() => ForgeProjectWorkspace.OpenAsync(movedPath));
            var rejectedContent = await File.ReadAllBytesAsync(contentPath);
            Equal(true, futureContent.SequenceEqual(rejectedContent), "future content remains unchanged");
            await File.WriteAllBytesAsync(contentPath, contentAfter);

            var sharedEdit = await project.ApplyAssetEditAsync(firstId, "shared edit"u8.ToArray(), catalog);
            Equal(2, sharedEdit.Changes.Count, "default edit updates project references");
            Equal(true, project.Content.Entities.All(entity => entity.Asset!.Id == sharedEdit.DerivedAssetId), "shared references redirected");
            Equal(true, project.ResolveAssetPath(sharedEdit.DerivedAssetId, catalog)!.StartsWith(movedPath, StringComparison.Ordinal), "project asset resolves before global catalog");
            currentGlobalHash = SHA256.HashData(await File.ReadAllBytesAsync(globalBlob));
            Equal(true, globalHash.SequenceEqual(currentGlobalHash), "copy-on-write preserves global blob");

            project.UndoAssetEdit(sharedEdit);
            Equal(true, project.Content.Entities.All(entity => entity.Asset!.Id == global.Id), "undo restores global references");
            Equal(false, project.IsAssetReferenced(sharedEdit.DerivedAssetId), "undone asset is cleanup eligible");

            var uniqueEdit = await project.ApplyAssetEditAsync(firstId, "unique edit"u8.ToArray(), catalog, makeUnique: true);
            Equal(1, uniqueEdit.Changes.Count, "make unique changes one reference");
            Equal(uniqueEdit.DerivedAssetId, project.Content.Entities.Single(entity => entity.EntityId == firstId).Asset!.Id, "selected reference isolated");
            Equal(global.Id, project.Content.Entities.Single(entity => entity.EntityId == secondId).Asset!.Id, "sibling reference remains global");
            Equal(true, otherProject.Content.Entities.All(entity => entity.Asset!.Id == global.Id), "other project remains isolated");
            Equal(0, otherProject.Content.Assets.Count, "other project receives no attached assets");

            await project.SaveAsync();
            var reopened = await ForgeProjectWorkspace.OpenAsync(movedPath);
            Equal(uniqueEdit.DerivedAssetId, reopened.Content.Entities.Single(entity => entity.EntityId == firstId).Asset!.Id, "override survives save and reopen");
            Equal(true, File.Exists(reopened.ResolveAssetPath(uniqueEdit.DerivedAssetId, catalog)), "reopened override resolves");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ProjectEntity Entity(EntityId id, string name, AssetId assetId) => new(
        id,
        name,
        "mobys",
        ProjectTransform.Identity,
        new(assetId, AssetKind.Moby));

    private static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected {expected}, got {actual}");
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }
}
