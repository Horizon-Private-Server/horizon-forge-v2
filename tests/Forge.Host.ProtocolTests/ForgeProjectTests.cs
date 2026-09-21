using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
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
            var baseLevel = new ProjectBaseLevel(
                "UYA", "NTSC-U", "1.00", 3, UyaIsoService.SupportedMd5,
                EntityVersion: ProjectSchema.CurrentBaseEntityVersion);
            var originalPath = Path.Combine(root, "project-a");
            var otherPath = Path.Combine(root, "project-b");
            var project = await ForgeProjectWorkspace.CreateAsync(originalPath, "Portable project", target, baseLevel, entities);
            var otherProject = await ForgeProjectWorkspace.CreateAsync(otherPath, "Other project", target, baseLevel, entities);
            Equal(false, project.IsDirty, "new project is clean after creation");
            Equal(false, project.MigrationPending, "new project needs no migration");

            project.UpdateTransform(firstId, ProjectTransform.Identity with { Position = new(1, 2, 3) });
            Equal(true, project.IsDirty, "transform marks project dirty");
            Equal(0, project.Content.Assets.Count, "transform does not copy assets");
            Equal(global.Id, project.Content.Entities[0].Asset!.Id, "transform retains global reference");
            var currentGlobalHash = SHA256.HashData(await File.ReadAllBytesAsync(globalBlob));
            Equal(true, globalHash.SequenceEqual(currentGlobalHash), "global blob unchanged");
            await project.SaveAsync();
            Equal(false, project.IsDirty, "manual save marks project clean");
            project.UpdateTransform(firstId, ProjectTransform.Identity with { Position = new(4, 5, 6) });
            project.UpdateTransform(firstId, ProjectTransform.Identity with { Position = new(1, 2, 3) });
            Equal(false, project.IsDirty, "returning to saved fingerprint marks project clean");

            var cancelledPath = Path.Combine(root, "cancelled-project");
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                await ThrowsAsync<OperationCanceledException>(() => ForgeProjectWorkspace.CreateAsync(
                    cancelledPath, "Cancelled", target, baseLevel, entities, cancellation.Token));
            }
            Equal(false, File.Exists(Path.Combine(cancelledPath, ForgeProjectWorkspace.ManifestFileName)),
                "cancelled initial save leaves no manifest");
            Equal(false, File.Exists(Path.Combine(cancelledPath, ForgeProjectWorkspace.DefaultContentPath)),
                "cancelled initial save leaves no content");

            var manifestBefore = await File.ReadAllBytesAsync(Path.Combine(originalPath, ForgeProjectWorkspace.ManifestFileName));
            var contentBefore = await File.ReadAllBytesAsync(Path.Combine(originalPath, ForgeProjectWorkspace.DefaultContentPath));
            Equal(false, System.Text.Encoding.UTF8.GetString(manifestBefore).Contains(root, StringComparison.Ordinal), "manifest contains no machine path");
            var movedPath = Path.Combine(root, "moved-project-a");
            Directory.Move(originalPath, movedPath);
            project = await ForgeProjectWorkspace.OpenAsync(movedPath);
            Equal(false, project.IsDirty, "reopened project is clean");
            await project.SaveAsync();
            var manifestAfter = await File.ReadAllBytesAsync(Path.Combine(movedPath, ForgeProjectWorkspace.ManifestFileName));
            var contentAfter = await File.ReadAllBytesAsync(Path.Combine(movedPath, ForgeProjectWorkspace.DefaultContentPath));
            Equal(true, manifestBefore.SequenceEqual(manifestAfter), "manifest deterministic round trip");
            Equal(true, contentBefore.SequenceEqual(contentAfter), "content deterministic round trip");
            Equal(firstId, project.Content.Entities[0].EntityId, "entity ID survives move and reopen");

            var contentPath = Path.Combine(movedPath, ForgeProjectWorkspace.DefaultContentPath);
            var futureContent = "{\"schemaVersion\":2,\"futureData\":true}"u8.ToArray();
            await File.WriteAllBytesAsync(contentPath, futureContent);
            await ThrowsAsync<UnsupportedProjectSchemaException>(() => ForgeProjectWorkspace.OpenAsync(movedPath));
            var rejectedContent = await File.ReadAllBytesAsync(contentPath);
            Equal(true, futureContent.SequenceEqual(rejectedContent), "future content remains unchanged");
            await File.WriteAllBytesAsync(contentPath, contentAfter);

            project.UpdateTransform(firstId, ProjectTransform.Identity with { Position = new(9, 8, 7) });
            var firstRecovery = await project.WriteRecoveryAsync()
                ?? throw new InvalidOperationException("Expected dirty recovery snapshot");
            project.UpdateTransform(firstId, ProjectTransform.Identity with { Position = new(6, 5, 4) });
            var secondRecovery = await project.WriteRecoveryAsync()
                ?? throw new InvalidOperationException("Expected second recovery snapshot");
            Equal(2, (await project.ListRecoveriesAsync()).Count, "recovery snapshots listed");
            var explicitProject = await ForgeProjectWorkspace.OpenAsync(movedPath);
            Equal(new ProjectVector3(1, 2, 3), explicitProject.Content.Entities[0].Transform.Position,
                "autosave does not replace explicit save");
            await project.LoadRecoveryAsync(firstRecovery.Id);
            Equal(new ProjectVector3(9, 8, 7), project.Content.Entities[0].Transform.Position, "recovery preview loads selected state");
            Equal(true, project.IsDirty, "loaded recovery remains dirty until explicit save");
            await project.SaveAsync();
            Equal(false, project.IsDirty, "saving recovered state marks clean");
            Equal(null, await project.WriteRecoveryAsync(), "clean project does not autosave");

            for (var index = 0; index < ForgeProjectWorkspace.MaxRecoverySnapshots + 2; index++)
            {
                project.UpdateTransform(firstId, ProjectTransform.Identity with { Position = new(index, index + 1, index + 2) });
                await project.WriteRecoveryAsync();
            }
            var boundedRecoveries = await project.ListRecoveriesAsync();
            Equal(ForgeProjectWorkspace.MaxRecoverySnapshots, boundedRecoveries.Count, "recovery count bound");
            var oversized = boundedRecoveries[^1];
            await using (var padding = new FileStream(
                Path.Combine(movedPath, ForgeProjectWorkspace.RecoveryDirectoryName, oversized.Id, "padding"),
                FileMode.Create, FileAccess.Write, FileShare.None))
            {
                padding.SetLength(ForgeProjectWorkspace.MaxRecoveryBytes);
            }
            project.UpdateTransform(firstId, ProjectTransform.Identity with { Position = new(99, 98, 97) });
            await project.WriteRecoveryAsync();
            boundedRecoveries = await project.ListRecoveriesAsync();
            Equal(false, boundedRecoveries.Any(snapshot => snapshot.Id == oversized.Id), "recovery disk-size pruning");
            Equal(true, boundedRecoveries.Sum(snapshot => snapshot.Size) <= ForgeProjectWorkspace.MaxRecoveryBytes,
                "recovery disk-size bound");

            await project.SaveAsync();
            var savedManifest = await File.ReadAllBytesAsync(Path.Combine(movedPath, ForgeProjectWorkspace.ManifestFileName));
            var savedContent = await File.ReadAllBytesAsync(contentPath);
            var journal = Path.Combine(movedPath, ForgeProjectWorkspace.RecoveryDirectoryName, ".save-journal");
            Directory.CreateDirectory(Path.Combine(journal, "content"));
            await File.WriteAllBytesAsync(Path.Combine(journal, ForgeProjectWorkspace.ManifestFileName), savedManifest);
            await File.WriteAllBytesAsync(Path.Combine(journal, ForgeProjectWorkspace.DefaultContentPath), savedContent);
            await File.WriteAllTextAsync(contentPath, "interrupted save");
            project = await ForgeProjectWorkspace.OpenAsync(movedPath);
            var restoredContent = await File.ReadAllBytesAsync(contentPath);
            Equal(true, savedContent.SequenceEqual(restoredContent), "interrupted save restores explicit content");
            Equal(false, Directory.Exists(journal), "completed journal recovery is removed");

            var legacyPath = Path.Combine(root, "legacy-project");
            Directory.CreateDirectory(Path.Combine(legacyPath, "content"));
            await WriteLegacyV0Async(savedManifest, Path.Combine(legacyPath, ForgeProjectWorkspace.ManifestFileName));
            await WriteLegacyV0Async(savedContent, Path.Combine(legacyPath, ForgeProjectWorkspace.DefaultContentPath));
            var legacy = await ForgeProjectWorkspace.OpenAsync(legacyPath);
            Equal(true, legacy.MigrationPending, "v0 project migration is pending");
            Equal(true, legacy.IsDirty, "migration marks project dirty");
            var legacyDescriptor = await UyaProjectService.InspectAsync(legacyPath, catalog);
            Equal(true, legacyDescriptor.MigrationPending, "project inspection offers migration");
            Equal(0, ReadSchemaVersion(await File.ReadAllBytesAsync(Path.Combine(legacyPath, ForgeProjectWorkspace.ManifestFileName))),
                "migration does not silently overwrite v0 manifest");
            legacyDescriptor = await UyaProjectService.MigrateAsync(legacyPath, catalog, string.Empty);
            Equal(false, legacyDescriptor.MigrationPending, "explicit migration completes upgrade");
            Equal(1, ReadSchemaVersion(await File.ReadAllBytesAsync(Path.Combine(legacyPath, ForgeProjectWorkspace.ManifestFileName))),
                "explicit save writes v1 manifest");

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

    private static async Task WriteLegacyV0Async(byte[] currentBytes, string path)
    {
        var document = JsonNode.Parse(currentBytes)?.AsObject()
            ?? throw new InvalidOperationException("Current project document is invalid");
        document["schemaVersion"] = 0;
        document.Remove("documentType");
        await File.WriteAllTextAsync(path, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    private static int ReadSchemaVersion(byte[] bytes) =>
        JsonNode.Parse(bytes)?["schemaVersion"]?.GetValue<int>()
        ?? throw new InvalidOperationException("Project document has no schema version");

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
