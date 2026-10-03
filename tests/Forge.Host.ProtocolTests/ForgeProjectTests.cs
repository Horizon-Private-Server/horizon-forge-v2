using Forge.Host.Games.UYA;
using System.Security.Cryptography;
using System.IO.Compression;
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
            var summary = await ForgeProjectWorkspace.SummarizeAsync(originalPath);
            Equal("Portable project", summary.Name, "project summary reads the manifest");
            Equal(false, summary.HasRecovery, "clean project summary has no recovery");

            var migrationPath = Path.Combine(root, "collision-migration");
            var migrating = await ForgeProjectWorkspace.CreateAsync(
                migrationPath,
                "Collision migration",
                target,
                baseLevel with { EntityVersion = ProjectSchema.CurrentBaseEntityVersion - 1 },
                []);
            var collisionAsset = new ProjectAssetReference(
                AssetId.Parse(new string('c', AssetId.TextLength)), AssetKind.Collision);
            var migratedPieces = new[]
            {
                new ProjectEntity(EntityId.New(), "Solid #0", "collision", ProjectTransform.Identity, collisionAsset,
                    new("UYA", 3, "collision/primary/solid", 0),
                    Collision: new(ProjectCollisionPieceKind.Solid, 0, 0, 1, 3, [new(0x21, 1)])),
                new ProjectEntity(EntityId.New(), "Solid #1", "collision", ProjectTransform.Identity, collisionAsset,
                    new("UYA", 3, "collision/primary/solid", 1),
                    Collision: new(ProjectCollisionPieceKind.Solid, 0, 1, 1, 3, [new(0x22, 1)])),
            };
            migrating.CompleteBaseEntityImport(migratedPieces, 0);
            Equal(2, migrating.Content.Entities.Count, "migration imports every collision piece from one payload");
            migrating.RemoveEntity(migratedPieces[0].EntityId);
            migrating.CompleteBaseEntityImport(migratedPieces, 0);
            Equal(1, migrating.Content.Entities.Count, "completed migration does not resurrect deleted collision");

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
            Equal(true, contentBefore is [0x1f, 0x8b, ..], "project content is gzip compressed");
            Equal(true, contentBefore.Length < Decompress(contentBefore).Length, "compressed project content is smaller");
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
            var futureContent = System.Text.Encoding.UTF8.GetBytes(
                $"{{\"schemaVersion\":{ProjectSchema.CurrentVersion + 1},\"futureData\":true}}");
            var storedFutureContent = Compress(futureContent);
            await File.WriteAllBytesAsync(contentPath, storedFutureContent);
            Equal("Portable project", (await ForgeProjectWorkspace.SummarizeAsync(movedPath)).Name,
                "project summary does not parse content");
            await ThrowsAsync<UnsupportedProjectSchemaException>(() => ForgeProjectWorkspace.OpenAsync(movedPath));
            var rejectedContent = await File.ReadAllBytesAsync(contentPath);
            Equal(true, storedFutureContent.SequenceEqual(rejectedContent), "future content remains unchanged");
            await File.WriteAllBytesAsync(contentPath, contentAfter);

            project.UpdateTransform(firstId, ProjectTransform.Identity with { Position = new(9, 8, 7) });
            var firstRecovery = await project.WriteRecoveryAsync()
                ?? throw new InvalidOperationException("Expected dirty recovery snapshot");
            project.UpdateTransform(firstId, ProjectTransform.Identity with { Position = new(6, 5, 4) });
            var secondRecovery = await project.WriteRecoveryAsync()
                ?? throw new InvalidOperationException("Expected second recovery snapshot");
            Equal(2, (await project.ListRecoveriesAsync()).Count, "recovery snapshots listed");
            Equal(true, (await ForgeProjectWorkspace.SummarizeAsync(movedPath)).HasRecovery,
                "project summary detects newer recovery without loading it");
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
            var savedContentJson = Decompress(savedContent);
            await WriteLegacyManifestAsync(savedManifest, Path.Combine(legacyPath, ForgeProjectWorkspace.ManifestFileName), 0);
            await WriteSchemaVersionAsync(savedContentJson, Path.Combine(legacyPath, ForgeProjectWorkspace.LegacyContentPath), 0, true);
            var legacy = await ForgeProjectWorkspace.OpenAsync(legacyPath);
            Equal(true, legacy.MigrationPending, "v0 project migration is pending");
            Equal(true, legacy.IsDirty, "migration marks project dirty");
            var legacyDescriptor = await UyaProjectService.InspectAsync(legacyPath, catalog);
            Equal(true, legacyDescriptor.MigrationPending, "project inspection offers migration");
            Equal(0, ReadSchemaVersion(await File.ReadAllBytesAsync(Path.Combine(legacyPath, ForgeProjectWorkspace.ManifestFileName))),
                "migration does not silently overwrite v0 manifest");
            await legacy.SaveAsync();
            Equal(false, File.Exists(Path.Combine(legacyPath, ForgeProjectWorkspace.LegacyContentPath)),
                "explicit save removes legacy content");
            Equal(true, File.Exists(Path.Combine(legacyPath, ForgeProjectWorkspace.DefaultContentPath)),
                "explicit save writes compressed content");
            legacyDescriptor = await UyaProjectService.InspectAsync(legacyPath, catalog);
            Equal(true, legacyDescriptor.MigrationPending,
                "schema-only migration still requires UYA source-backed content upgrade");
            Equal(ProjectSchema.CurrentVersion,
                ReadSchemaVersion(await File.ReadAllBytesAsync(Path.Combine(legacyPath, ForgeProjectWorkspace.ManifestFileName))),
                "explicit save writes current manifest");

            var versionOnePath = Path.Combine(root, "version-one-project");
            Directory.CreateDirectory(Path.Combine(versionOnePath, "content"));
            await WriteLegacyManifestAsync(savedManifest, Path.Combine(versionOnePath, ForgeProjectWorkspace.ManifestFileName), 1);
            await WriteSchemaVersionAsync(savedContentJson, Path.Combine(versionOnePath, ForgeProjectWorkspace.LegacyContentPath), 1);
            var versionOne = await ForgeProjectWorkspace.OpenAsync(versionOnePath);
            Equal(true, versionOne.MigrationPending, "v1 project migration is pending");
            await versionOne.SaveAsync();
            Equal(ProjectSchema.CurrentVersion,
                ReadSchemaVersion(await File.ReadAllBytesAsync(Path.Combine(versionOnePath, ForgeProjectWorkspace.ManifestFileName))),
                "explicit save migrates v1 manifest");

            var versionFourPath = Path.Combine(root, "version-four-project");
            Directory.CreateDirectory(Path.Combine(versionFourPath, "content"));
            await WriteLegacyManifestAsync(
                savedManifest,
                Path.Combine(versionFourPath, ForgeProjectWorkspace.ManifestFileName),
                4);
            await WriteSchemaVersionAsync(
                savedContentJson,
                Path.Combine(versionFourPath, ForgeProjectWorkspace.LegacyContentPath),
                4,
                compress: true);
            var versionFour = await ForgeProjectWorkspace.OpenAsync(versionFourPath);
            Equal(true, versionFour.MigrationPending, "v4 project migration is pending");
            Equal(0, versionFour.Content.TieCollisionBindings.Count, "v4 migration supplies an empty proxy binding list");

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
            var chainedEdit = await project.ApplyAssetEditAsync(
                firstId, "chained unique edit"u8.ToArray(), catalog, makeUnique: true);

            await project.SaveAsync();
            var reopened = await ForgeProjectWorkspace.OpenAsync(movedPath);
            Equal(chainedEdit.DerivedAssetId, reopened.Content.Entities.Single(entity => entity.EntityId == firstId).Asset!.Id, "override survives save and reopen");
            Equal(true, File.Exists(reopened.ResolveAssetPath(chainedEdit.DerivedAssetId, catalog)), "reopened override resolves");

            var tie = await catalog.PutAsync(
                AssetKind.Tie,
                canonicalFormatVersion: 0,
                "exact tie variant"u8.ToArray(),
                new(
                    "test-importer",
                    new("UYA", "NTSC-U", "1.00", "level03", "level_wad/assets/tie.bin", 8, UyaIsoService.SupportedMd5)));
            var firstTieId = EntityId.New();
            var secondTieId = EntityId.New();
            reopened.AddEntity(Entity(firstTieId, "First TIE", tie.Id, AssetKind.Tie));
            reopened.AddEntity(Entity(secondTieId, "Second TIE", tie.Id, AssetKind.Tie));
            var recipe = new ProjectTieCollisionRecipe(
                ProjectTieCollisionRecipeKind.Wrap,
                GeneratorVersion: 5,
                RecipeVersion: 1,
                LodIndex: 0,
                RawType: 0x31,
                DetailSize: 1,
                SealOpeningSize: 2);
            var assetCountBeforeProxy = reopened.Content.Assets.Count;
            var binding = await reopened.ApplyTieCollisionProxyAsync(
                tie.Id,
                "proxy geometry"u8.ToArray(),
                canonicalFormatVersion: 1,
                recipe);
            Equal(1, reopened.Content.TieCollisionBindings.Count, "matching TIEs share one proxy binding");
            Equal(assetCountBeforeProxy + 1, reopened.Content.Assets.Count, "proxy geometry is attached once");
            Equal(tie.Id,
                reopened.Content.Assets.Single(asset => asset.Id == binding.ProxyAssetId).ParentId,
                "proxy parent is the exact TIE Asset ID");
            Equal(true, reopened.IsAssetReferenced(binding.ProxyAssetId), "bound proxy is protected from collection");
            _ = await reopened.ApplyTieCollisionProxyAsync(
                tie.Id,
                "proxy geometry"u8.ToArray(),
                canonicalFormatVersion: 1,
                recipe);
            Equal(assetCountBeforeProxy + 1, reopened.Content.Assets.Count, "reapplying equal geometry deduplicates its blob");
            reopened.SetTieCollisionEnabled(secondTieId, false);
            Equal(false,
                reopened.Content.Entities.Single(entity => entity.EntityId == secondTieId).TieCollisionEnabled,
                "one matching TIE can opt out without copying proxy geometry");
            var futureTieId = EntityId.New();
            reopened.AddEntity(Entity(futureTieId, "Future TIE", tie.Id, AssetKind.Tie));
            Equal(1, reopened.Content.TieCollisionBindings.Count,
                "future placement inherits the exact Asset-ID binding without another record");
            var fingerprintBeforeInvalidRecipe = reopened.CurrentFingerprint;
            await ThrowsAsync<InvalidDataException>(() => reopened.ApplyTieCollisionProxyAsync(
                tie.Id,
                "invalid proxy"u8.ToArray(),
                canonicalFormatVersion: 1,
                recipe with { DetailSize = 0 }));
            Equal(fingerprintBeforeInvalidRecipe, reopened.CurrentFingerprint,
                "invalid recipe preserves the last known-good project state");

            await reopened.SaveAsync();
            reopened = await ForgeProjectWorkspace.OpenAsync(movedPath);
            Equal(binding, reopened.Content.TieCollisionBindings.Single(), "proxy binding survives save and reopen");
            Equal(false,
                reopened.Content.Entities.Single(entity => entity.EntityId == secondTieId).TieCollisionEnabled,
                "per-instance proxy opt-out survives save and reopen");
            Equal(true, File.Exists(reopened.ResolveAssetPath(binding.ProxyAssetId, catalog)),
                "reopened proxy resolves from its portable project-relative blob");

            var replacementBytes = "replacement proxy geometry"u8.ToArray();
            var replacement = await reopened.ApplyTieCollisionProxyAsync(
                tie.Id,
                replacementBytes,
                canonicalFormatVersion: 1,
                recipe with { SealOpeningSize = 4 });
            Equal(false, reopened.IsAssetReferenced(binding.ProxyAssetId),
                "replaced proxy becomes eligible for safe collection");
            Equal(true, reopened.IsAssetReferenced(replacement.ProxyAssetId),
                "replacement proxy remains protected");
            await ThrowsAsync<InvalidOperationException>(() => reopened.CollectUnreferencedAssetsAsync());
            var proxyRecovery = await reopened.WriteRecoveryAsync()
                ?? throw new InvalidOperationException("Expected proxy recovery snapshot");
            await reopened.SaveAsync();
            var originalProxyId = binding.ProxyAssetId.ToString();
            var originalProxyPath = Path.Combine(
                movedPath, "assets", originalProxyId[..2], $"{originalProxyId}.blob");
            var collected = await reopened.CollectUnreferencedAssetsAsync();
            Equal(true, collected.ToHashSet().SetEquals([sharedEdit.DerivedAssetId, binding.ProxyAssetId]),
                "collection removes every unreferenced attached asset");
            Equal(false, File.Exists(originalProxyPath), "collection deletes the replaced proxy blob");
            Equal(false, reopened.Content.Assets.Any(asset => asset.Id == binding.ProxyAssetId),
                "collection deletes the replaced proxy metadata");
            Equal(true, reopened.Content.Assets.Any(asset => asset.Id == uniqueEdit.DerivedAssetId),
                "collection protects a referenced asset's attached parent");
            Equal(true, reopened.Content.Assets.Any(asset => asset.Id == replacement.ProxyAssetId),
                "collection protects the active proxy metadata");
            reopened.RemoveTieCollisionProxy(tie.Id);
            Equal(false, reopened.IsAssetReferenced(replacement.ProxyAssetId),
                "removed proxy becomes eligible for safe collection");
            await reopened.SaveAsync();
            Equal(0, (await reopened.CollectUnreferencedAssetsAsync()).Count,
                "collection protects a proxy referenced by recovery");
            Equal(true, File.Exists(reopened.ResolveAttachedAssetPath(replacement.ProxyAssetId)),
                "recovery-protected proxy blob survives collection");
            await reopened.LoadRecoveryAsync(proxyRecovery.Id);
            Equal(replacement, reopened.Content.TieCollisionBindings.Single(),
                "recovery restores the reusable proxy binding");
            await reopened.SaveAsync();

            var archivePath = Path.Combine(root, "portable-project.zip");
            ZipFile.CreateFromDirectory(movedPath, archivePath, CompressionLevel.Fastest, false);
            var transferredPath = Path.Combine(root, "transferred-project");
            ZipFile.ExtractToDirectory(archivePath, transferredPath);
            var transferred = await ForgeProjectWorkspace.OpenAsync(transferredPath);
            Equal(replacement, transferred.Content.TieCollisionBindings.Single(),
                "zip transfer preserves the exact proxy binding");
            var transferredProxyPath = transferred.ResolveAttachedAssetPath(replacement.ProxyAssetId)
                ?? throw new InvalidOperationException("Transferred proxy blob did not resolve");
            var transferredProxyBytes = await File.ReadAllBytesAsync(transferredProxyPath);
            Equal(true, replacementBytes.SequenceEqual(transferredProxyBytes),
                "zip transfer preserves the project-relative proxy blob");

            var repairedCatalog = await AssetCatalogStore.OpenAsync(Path.Combine(root, "repaired-catalog"));
            Equal(null, transferred.ResolveAssetPath(tie.Id, repairedCatalog),
                "transferred project initially reports its missing source TIE");
            var repairedTie = await repairedCatalog.PutAsync(
                AssetKind.Tie,
                canonicalFormatVersion: 0,
                "exact tie variant"u8.ToArray(),
                new(
                    "test-repair",
                    new("UYA", "NTSC-U", "1.00", "level03", "level_wad/assets/tie.bin", 8,
                        UyaIsoService.SupportedMd5)));
            Equal(tie.Id, repairedTie.Id, "repair restores the same content-addressed TIE ID");
            Equal(true, transferred.ResolveAssetPath(tie.Id, repairedCatalog) is not null,
                "repaired source TIE resolves after transfer");
            Equal(replacement, transferred.Content.TieCollisionBindings.Single(),
                "source repair leaves the proxy binding unchanged");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ProjectEntity Entity(
        EntityId id,
        string name,
        AssetId assetId,
        AssetKind kind = AssetKind.Moby) => new(
        id,
        name,
        kind == AssetKind.Tie ? "ties" : "mobys",
        ProjectTransform.Identity,
        new(assetId, kind));

    private static async Task WriteLegacyManifestAsync(byte[] currentBytes, string path, int version)
    {
        var document = JsonNode.Parse(currentBytes)?.AsObject()
            ?? throw new InvalidOperationException("Current project document is invalid");
        document["schemaVersion"] = version;
        document["content"] = ForgeProjectWorkspace.LegacyContentPath;
        if (version == 0) document.Remove("documentType");
        await File.WriteAllTextAsync(path, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    private static async Task WriteSchemaVersionAsync(
        byte[] currentBytes,
        string path,
        int version,
        bool legacyV0 = false,
        bool compress = false)
    {
        var document = JsonNode.Parse(currentBytes)?.AsObject()
            ?? throw new InvalidOperationException("Current project document is invalid");
        document["schemaVersion"] = version;
        if (version < 5) document.Remove("tieCollisionBindings");
        if (legacyV0)
        {
            document.Remove("documentType");
        }
        var bytes = System.Text.Encoding.UTF8.GetBytes(
            document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        await File.WriteAllBytesAsync(path, compress ? Compress(bytes) : bytes);
    }

    private static byte[] Compress(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true)) gzip.Write(bytes);
        return output.ToArray();
    }

    private static byte[] Decompress(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
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
