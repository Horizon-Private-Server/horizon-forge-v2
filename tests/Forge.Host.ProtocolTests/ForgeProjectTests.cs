using Forge.Host.Games.UYA;
using System.Security.Cryptography;
using System.IO.Compression;
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
                "UYA", "NTSC-U", "1.00", 3, UyaIsoService.SupportedMd5);
            var originalPath = Path.Combine(root, "project-a");
            var otherPath = Path.Combine(root, "project-b");
            var project = await ForgeProjectWorkspace.CreateAsync(originalPath, "Portable project", target, baseLevel, entities);
            var otherProject = await ForgeProjectWorkspace.CreateAsync(otherPath, "Other project", target, baseLevel, entities);
            Equal(false, project.IsDirty, "new project is clean after creation");
            Equal(ProjectPaletteOptimization.Default, project.Manifest.Target.PaletteOptimization,
                "new projects store the default palette optimization profile");
            var defaultPaletteFingerprint = project.CurrentFingerprint;
            project.UpdatePaletteOptimization(new(ProjectPaletteOptimization.CurrentMappingVersion, 75));
            Equal(true, project.IsDirty, "palette optimization changes the project fingerprint");
            Equal(75, project.Manifest.Target.PaletteOptimization.Strength,
                "palette optimization strength updates project data");
            project.UpdatePaletteOptimization(ProjectPaletteOptimization.Default);
            Equal(defaultPaletteFingerprint, project.CurrentFingerprint,
                "restoring the palette profile restores the project fingerprint");
            Throws<ArgumentException>(() => project.UpdatePaletteOptimization(new("paletteOptimization.v2", 50)));
            Throws<ArgumentOutOfRangeException>(() => project.UpdatePaletteOptimization(
                new(ProjectPaletteOptimization.CurrentMappingVersion, 101)));
            var summary = await ForgeProjectWorkspace.SummarizeAsync(originalPath);
            Equal("Portable project", summary.Name, "project summary reads the manifest");
            Equal(false, summary.HasRecovery, "clean project summary has no recovery");

            var collisionAsset = new ProjectAssetReference(
                AssetId.Parse(new string('c', AssetId.TextLength)), AssetKind.Collision);
            var collisionProjectPath = Path.Combine(root, "collision-project");
            var existingTieId = EntityId.New();
            var tieAsset = new ProjectAssetReference(
                AssetId.Parse(new string('d', AssetId.TextLength)), AssetKind.Tie);
            var existingTie = new ProjectEntity(
                existingTieId, "Existing TIE", "ties", ProjectTransform.Identity, tieAsset,
                new("UYA", 3, "gameplay/core/tie_instances", 0));
            var linkedCollision = new ProjectEntity(
                EntityId.New(), "Linked solid", "collision", ProjectTransform.Identity, collisionAsset,
                new("UYA", 3, "collision/primary/solid", 0),
                Collision: new(
                    ProjectCollisionPieceKind.Solid, 0, 0, 1, 3, [new(0x21, 1)],
                    new(existingTieId, existingTie.Transform)));
            var collisionProject = await ForgeProjectWorkspace.CreateAsync(
                collisionProjectPath, "Collision project", target, baseLevel, [existingTie, linkedCollision]);
            collisionProject.UpdateTransform(linkedCollision.EntityId,
                linkedCollision.Transform with { Position = new(2, 3, 4) });
            Equal(new ProjectVector3(2, 3, 4),
                collisionProject.Content.Entities.Single(value => value.EntityId == existingTieId).Transform.Position,
                "moving recovered collision translates its attached TIE");
            Equal(ProjectTransform.Identity.Position,
                collisionProject.Content.Entities.Single(value => value.EntityId == linkedCollision.EntityId)
                    .Transform.Position,
                "reverse movement does not apply the translation twice to recovered collision");
            await collisionProject.SaveAsync();
            collisionProject = await ForgeProjectWorkspace.OpenAsync(collisionProjectPath);
            _ = collisionProject.RemoveEntities([existingTieId]);
            Equal(0, collisionProject.Content.Entities.Count,
                "deleting a TIE also deletes its recovered collision pieces");

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

            var currentVersion = $"\"schemaVersion\":{ProjectSchema.CurrentVersion}";
            var previousManifest = System.Text.Encoding.UTF8.GetString(manifestAfter)
                .Replace(currentVersion, $"\"schemaVersion\":{ProjectSchema.OldestSupportedVersion}", StringComparison.Ordinal)
                .Replace(
                    $",\"paletteOptimization\":{{\"mappingVersion\":\"{ProjectPaletteOptimization.CurrentMappingVersion}\",\"strength\":{ProjectPaletteOptimization.DefaultStrength}}}",
                    string.Empty,
                    StringComparison.Ordinal);
            var previousContent = System.Text.Encoding.UTF8.GetString(Decompress(contentAfter))
                .Replace(currentVersion, $"\"schemaVersion\":{ProjectSchema.OldestSupportedVersion}", StringComparison.Ordinal)
                .Replace("\"assetOverrides\":[],", string.Empty, StringComparison.Ordinal)
                .Replace(",\"groups\":[]", string.Empty, StringComparison.Ordinal);
            Equal(false, previousContent.Contains("assetOverrides", StringComparison.Ordinal),
                "previous schema fixture predates asset overrides");
            Equal(false, previousContent.Contains("groups", StringComparison.Ordinal),
                "previous schema fixture predates groups");
            await File.WriteAllTextAsync(Path.Combine(movedPath, ForgeProjectWorkspace.ManifestFileName), previousManifest);
            await File.WriteAllBytesAsync(Path.Combine(movedPath, ForgeProjectWorkspace.DefaultContentPath),
                Compress(System.Text.Encoding.UTF8.GetBytes(previousContent)));
            project = await ForgeProjectWorkspace.OpenAsync(movedPath);
            Equal(ProjectSchema.CurrentVersion, project.Manifest.SchemaVersion, "previous manifest schema migrates in memory");
            Equal(ProjectPaletteOptimization.Default, project.Manifest.Target.PaletteOptimization,
                "previous manifest receives the default palette optimization profile");
            Equal(ProjectSchema.CurrentVersion, project.Content.SchemaVersion, "previous content schema migrates in memory");
            Equal(0, project.Content.Groups.Count, "previous content receives an empty group list");
            Equal(false, project.IsDirty, "in-memory schema migration opens cleanly");
            Equal(true, (await File.ReadAllTextAsync(Path.Combine(movedPath, ForgeProjectWorkspace.ManifestFileName)))
                .Contains($"\"schemaVersion\":{ProjectSchema.OldestSupportedVersion}", StringComparison.Ordinal),
                "migration does not rewrite the project before explicit save");
            await project.SaveAsync();
            manifestAfter = await File.ReadAllBytesAsync(Path.Combine(movedPath, ForgeProjectWorkspace.ManifestFileName));
            contentAfter = await File.ReadAllBytesAsync(Path.Combine(movedPath, ForgeProjectWorkspace.DefaultContentPath));

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
            var manifestPath = Path.Combine(movedPath, ForgeProjectWorkspace.ManifestFileName);
            var mismatchedManifest = System.Text.Encoding.UTF8.GetString(manifestAfter)
                .Replace(currentVersion, "\"schemaVersion\":7", StringComparison.Ordinal);
            Equal(false, mismatchedManifest.Contains(currentVersion, StringComparison.Ordinal),
                "mismatched schema fixture changes the manifest version");
            await File.WriteAllTextAsync(manifestPath, mismatchedManifest);
            await ThrowsAsync<InvalidDataException>(() => ForgeProjectWorkspace.OpenAsync(movedPath));
            await File.WriteAllBytesAsync(manifestPath, manifestAfter);

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

            var sharedEdit = await project.ApplyAssetEditAsync(firstId, "shared edit"u8.ToArray(), catalog);
            Equal(2, sharedEdit.Changes.Count, "default edit updates project references");
            Equal(true, project.Content.Entities.All(entity => entity.Asset!.Id == sharedEdit.DerivedAssetId), "shared references redirected");
            Equal(true, project.ResolveAssetPath(new(sharedEdit.DerivedAssetId, AssetKind.Moby), catalog)!
                .StartsWith(movedPath, StringComparison.Ordinal), "project asset resolves before global catalog");
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
            Equal(true, File.Exists(reopened.ResolveAssetPath(
                new(chainedEdit.DerivedAssetId, AssetKind.Moby), catalog)), "reopened override resolves");

            var textureBytes = "standalone HUD texture"u8.ToArray();
            var texture = await reopened.AttachAssetAsync(AssetKind.Texture, 2, textureBytes);
            var tfragBytes = "standalone tfrag piece"u8.ToArray();
            var tfrag = await reopened.AttachAssetAsync(AssetKind.Tfrag, 3, tfragBytes);
            Equal(null, texture.ParentId, "brand-new attached texture has no invented parent");
            Equal(null, tfrag.ParentId, "brand-new attached tfrag has no invented parent");
            var attachedCount = reopened.Content.Assets.Count;
            Equal(texture, await reopened.AttachAssetAsync(AssetKind.Texture, 2, textureBytes),
                "equal standalone asset deduplicates");
            Equal(attachedCount, reopened.Content.Assets.Count, "deduplication does not add metadata");
            reopened.AddEntity(new(
                EntityId.New(), "HUD texture fixture", "hud", ProjectTransform.Identity,
                new(texture.Id, texture.Kind)));
            reopened.AddEntity(new(
                EntityId.New(), "Tfrag fixture", "tfrags", ProjectTransform.Identity,
                new(tfrag.Id, tfrag.Kind)));
            Equal(0, otherProject.Content.Assets.Count, "standalone attachment does not mutate another project");

            var fingerprintBeforeCancelledAttach = reopened.CurrentFingerprint;
            await ThrowsAsync<ArgumentException>(() => reopened.AttachAssetAsync(
                AssetKind.Texture, 2, ReadOnlyMemory<byte>.Empty));
            await ThrowsAsync<ArgumentOutOfRangeException>(() => reopened.AttachAssetAsync(
                (AssetKind)ushort.MaxValue, 2, "unknown kind"u8.ToArray()));
            Equal(fingerprintBeforeCancelledAttach, reopened.CurrentFingerprint,
                "malformed attachment preserves project metadata");
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                await ThrowsAsync<OperationCanceledException>(() => reopened.AttachAssetAsync(
                    AssetKind.Texture, 2, "cancelled HUD texture"u8.ToArray(), cancellationToken: cancellation.Token));
            }
            Equal(fingerprintBeforeCancelledAttach, reopened.CurrentFingerprint,
                "cancelled attachment preserves project metadata");

            var texturePath = reopened.ResolveAttachedAssetPath(texture.Id)
                ?? throw new InvalidOperationException("Attached texture did not resolve");
            var corruptTexture = textureBytes.ToArray();
            corruptTexture[0] ^= 0xff;
            await File.WriteAllBytesAsync(texturePath, corruptTexture);
            await ThrowsAsync<InvalidDataException>(() => reopened.ReadAttachedAssetVerifiedAsync(
                new(texture.Id, texture.Kind), ForgeProjectWorkspace.MaxAttachedAssetBytes));
            await ThrowsAsync<InvalidDataException>(() => reopened.AttachAssetAsync(AssetKind.Texture, 2, textureBytes));
            Equal(fingerprintBeforeCancelledAttach, reopened.CurrentFingerprint,
                "failed integrity check preserves project metadata");
            await File.WriteAllBytesAsync(texturePath, textureBytes);

            var sourceReference = new ProjectAssetReference(global.Id, AssetKind.Moby);
            var firstOverrideBytes = "first exact moby override"u8.ToArray();
            var firstOverride = await reopened.AttachAssetAsync(
                AssetKind.Moby, 0, firstOverrideBytes, global.Id);
            var secondOverrideBytes = "second exact moby override"u8.ToArray();
            var secondOverride = await reopened.AttachAssetAsync(
                AssetKind.Moby, 0, secondOverrideBytes, global.Id);
            var history = new EditorHistory();
            var beforeOverride = reopened.CaptureState();
            var fingerprintBeforeOverride = reopened.CurrentFingerprint;
            var firstBinding = reopened.SetAssetOverride(
                sourceReference, new(firstOverride.Id, firstOverride.Kind));
            var afterFirstOverride = reopened.CaptureState();
            history.Push(beforeOverride, afterFirstOverride, [], [], [secondId], 512);
            Equal(global.Id,
                reopened.Content.Entities.Single(entity => entity.EntityId == secondId).Asset!.Id,
                "override leaves the source entity reference unchanged");
            Equal(firstOverride.Id, reopened.ResolveAssetReference(sourceReference).Id,
                "exact override resolves to its first replacement");
            Equal(true, reopened.CurrentFingerprint != fingerprintBeforeOverride,
                "override changes the project fingerprint");
            Equal(firstOverride.Id, reopened.ResolveAsset(sourceReference, catalog)!.Effective.Id,
                "resolved metadata uses the effective replacement identity");
            Equal(true, history.TryUndo(out var overrideState, out _, out _), "override add is undoable");
            reopened.RestoreState(overrideState);
            Equal(sourceReference, reopened.ResolveAssetReference(sourceReference),
                "undo restores vanilla resolution");
            Equal(true, history.TryRedo(out overrideState, out _, out _), "override add is redoable");
            reopened.RestoreState(overrideState);
            Equal(firstBinding, reopened.Content.AssetOverrides.Single(), "redo restores override binding");

            var beforeReplacement = reopened.CaptureState();
            var secondBinding = reopened.SetAssetOverride(
                sourceReference, new(secondOverride.Id, secondOverride.Kind));
            history.Push(beforeReplacement, reopened.CaptureState(), [], [], [secondId], 512);
            Equal(1, reopened.Content.AssetOverrides.Count, "replacement keeps one exact source binding");
            Equal(secondOverride.Id, reopened.ResolveAssetReference(sourceReference).Id,
                "replacement changes effective resolution");
            Equal(true, history.TryUndo(out overrideState, out _, out _), "override replacement is undoable");
            reopened.RestoreState(overrideState);
            Equal(firstOverride.Id, reopened.ResolveAssetReference(sourceReference).Id,
                "replacement undo restores the prior binding");
            Equal(true, history.TryRedo(out overrideState, out _, out _), "override replacement is redoable");
            reopened.RestoreState(overrideState);
            Equal(secondBinding, reopened.Content.AssetOverrides.Single(), "replacement redo restores the new binding");

            var beforeRemoval = reopened.CaptureState();
            _ = reopened.RemoveAssetOverride(sourceReference);
            history.Push(beforeRemoval, reopened.CaptureState(), [], [], [secondId], 512);
            Equal(sourceReference, reopened.ResolveAssetReference(sourceReference),
                "removing an override restores vanilla resolution");
            Equal(true, history.TryUndo(out overrideState, out _, out _), "override removal is undoable");
            reopened.RestoreState(overrideState);
            Equal(secondOverride.Id, reopened.ResolveAssetReference(sourceReference).Id,
                "removal undo restores replacement resolution");
            Equal(true, history.TryRedo(out overrideState, out _, out _), "override removal is redoable");
            reopened.RestoreState(overrideState);
            Equal(sourceReference, reopened.ResolveAssetReference(sourceReference),
                "removal redo restores vanilla resolution");
            Equal(true, history.TryUndo(out overrideState, out _, out _),
                "active replacement can be restored after removal redo");
            reopened.RestoreState(overrideState);

            var validOverrideState = reopened.CaptureState();
            await ThrowsAsync<InvalidDataException>(() => RestoreAsync(reopened, validOverrideState with
            {
                Content = validOverrideState.Content with
                {
                    AssetOverrides = [secondBinding, secondBinding],
                },
            }));
            await ThrowsAsync<InvalidDataException>(() => RestoreAsync(reopened, validOverrideState with
            {
                Content = validOverrideState.Content with
                {
                    AssetOverrides = [secondBinding with
                    {
                        SchemaVersion = ProjectAssetOverrideSchema.CurrentVersion + 1,
                    }],
                },
            }));
            await ThrowsAsync<InvalidDataException>(() => Task.FromResult(reopened.SetAssetOverride(
                sourceReference, new(texture.Id, texture.Kind))));
            await ThrowsAsync<InvalidDataException>(() => Task.FromResult(reopened.SetAssetOverride(
                sourceReference, sourceReference)));
            await ThrowsAsync<InvalidDataException>(() => Task.FromResult(reopened.SetAssetOverride(
                sourceReference, new(AssetId.Parse(new string('e', AssetId.TextLength)), AssetKind.Moby))));
            await ThrowsAsync<InvalidDataException>(() => Task.FromResult(reopened.SetAssetOverride(
                new(secondOverride.Id, secondOverride.Kind), new(firstOverride.Id, firstOverride.Kind))));
            Equal(validOverrideState, reopened.CaptureState(),
                "invalid override graphs preserve the last known-good project state");

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
            var recipe = new ProjectInstancedCollisionRecipe(
                ProjectInstancedCollisionRecipeKind.Wrap,
                GeneratorVersion: 5,
                RecipeVersion: 1,
                LodIndex: 0,
                RawType: 0x31,
                DetailSize: 1,
                SealOpeningSize: 2);
            var assetCountBeforeProxy = reopened.Content.Assets.Count;
            var binding = await reopened.ApplyInstancedCollisionProxyAsync(
                tie.Id,
                "proxy geometry"u8.ToArray(),
                canonicalFormatVersion: 1,
                recipe);
            Equal(1, reopened.Content.InstancedCollisionBindings.Count, "matching TIEs share one proxy binding");
            Equal(assetCountBeforeProxy + 1, reopened.Content.Assets.Count, "proxy geometry is attached once");
            Equal(tie.Id,
                reopened.Content.Assets.Single(asset => asset.Id == binding.ProxyAssetId).ParentId,
                "proxy parent is the exact TIE Asset ID");
            Equal(true, reopened.IsAssetReferenced(binding.ProxyAssetId), "bound proxy is protected from collection");
            _ = await reopened.ApplyInstancedCollisionProxyAsync(
                tie.Id,
                "proxy geometry"u8.ToArray(),
                canonicalFormatVersion: 1,
                recipe);
            Equal(assetCountBeforeProxy + 1, reopened.Content.Assets.Count, "reapplying equal geometry deduplicates its blob");
            reopened.SetInstancedCollisionEnabled(secondTieId, true);
            Equal(true,
                reopened.Content.Entities.Single(entity => entity.EntityId == secondTieId).InstancedCollisionEnabled,
                "one matching TIE can opt into shared collision without copying proxy geometry");
            var futureTieId = EntityId.New();
            reopened.AddEntity(Entity(futureTieId, "Future TIE", tie.Id, AssetKind.Tie));
            Equal(1, reopened.Content.InstancedCollisionBindings.Count,
                "future placement inherits the exact Asset-ID binding without another record");
            var fingerprintBeforeInvalidRecipe = reopened.CurrentFingerprint;
            await ThrowsAsync<InvalidDataException>(() => reopened.ApplyInstancedCollisionProxyAsync(
                tie.Id,
                "invalid proxy"u8.ToArray(),
                canonicalFormatVersion: 1,
                recipe with { DetailSize = 0 }));
            Equal(fingerprintBeforeInvalidRecipe, reopened.CurrentFingerprint,
                "invalid recipe preserves the last known-good project state");

            reopened.SetInstancedCollisionFaceTypes(
                tie.Id,
                binding.ProxyAssetId,
                [new(2, 0x11), new(0, 0x22)],
                faceCount: 3);
            binding = reopened.Content.InstancedCollisionBindings.Single();
            Equal(true, binding.FaceTypeOverrides.SequenceEqual([new(0, 0x22), new(2, 0x11)]),
                "proxy face types are stored sparsely in face order");

            await reopened.SaveAsync();
            reopened = await ForgeProjectWorkspace.OpenAsync(movedPath);
            EqualBinding(binding, reopened.Content.InstancedCollisionBindings.Single(), "proxy binding survives save and reopen");
            Equal(secondBinding, reopened.Content.AssetOverrides.Single(),
                "asset override survives save and reopen");
            var overridePath = reopened.ResolveAttachedAssetPath(secondOverride.Id)
                ?? throw new InvalidOperationException("Override replacement did not resolve");
            var missingOverridePath = overridePath + ".missing-test";
            File.Move(overridePath, missingOverridePath);
            try
            {
                await ThrowsAsync<InvalidDataException>(() => ForgeProjectWorkspace.OpenAsync(movedPath));
            }
            finally
            {
                File.Move(missingOverridePath, overridePath);
            }
            reopened = await ForgeProjectWorkspace.OpenAsync(movedPath);
            reopened.SetInstancedCollisionFaceTypes(
                tie.Id,
                binding.ProxyAssetId,
                [new(0, recipe.RawType), new(2, recipe.RawType)],
                faceCount: 3);
            binding = reopened.Content.InstancedCollisionBindings.Single();
            Equal(0, binding.FaceTypeOverrides.Count, "painting the default removes persisted overrides");
            Equal(true,
                reopened.Content.Entities.Single(entity => entity.EntityId == secondTieId).InstancedCollisionEnabled,
                "per-instance shared collision choice survives save and reopen");
            Equal(true, File.Exists(reopened.ResolveAssetPath(
                    new(binding.ProxyAssetId, AssetKind.Collision), catalog)),
                "reopened proxy resolves from its portable project-relative blob");

            var replacementBytes = "replacement proxy geometry"u8.ToArray();
            var replacement = await reopened.ApplyInstancedCollisionProxyAsync(
                tie.Id,
                replacementBytes,
                canonicalFormatVersion: 1,
                recipe with { SealOpeningSize = 4 });
            Equal(0, replacement.FaceTypeOverrides.Count,
                "replacing a proxy discards overrides from the old face-ID domain");
            Equal(false, reopened.IsAssetReferenced(binding.ProxyAssetId),
                "replaced proxy becomes eligible for safe collection");
            Equal(true, reopened.IsAssetReferenced(replacement.ProxyAssetId),
                "replacement proxy remains protected");
            await ThrowsAsync<InvalidOperationException>(() => reopened.CollectUnreferencedAssetsAsync());
            var proxyRecovery = await reopened.WriteRecoveryAsync()
                ?? throw new InvalidOperationException("Expected proxy recovery snapshot");
            await reopened.SaveAsync();
            var rollbackAsset = await reopened.AttachAssetAsync(
                AssetKind.Texture, 2, "collection rollback fixture"u8.ToArray());
            await reopened.SaveAsync();
            var rollbackPath = reopened.ResolveAttachedAssetPath(rollbackAsset.Id)
                ?? throw new InvalidOperationException("Collection rollback fixture did not resolve");
            var contentDirectory = Path.GetDirectoryName(reopened.ContentFilePath)!;
            var contentBackup = $"{contentDirectory}.backup";
            Directory.Move(contentDirectory, contentBackup);
            await File.WriteAllBytesAsync(contentDirectory, [0]);
            try
            {
                await ThrowsAsync<InvalidDataException>(() => reopened.CollectUnreferencedAssetsAsync());
                Equal(true, File.Exists(rollbackPath),
                    "failed collection preserves attached asset blobs");
                Equal(true, reopened.Content.Assets.Any(asset => asset.Id == rollbackAsset.Id),
                    "failed collection restores attached asset metadata");
            }
            finally
            {
                File.Delete(contentDirectory);
                Directory.Move(contentBackup, contentDirectory);
            }
            var originalProxyId = binding.ProxyAssetId.ToString();
            var originalProxyPath = Path.Combine(
                movedPath, "assets", originalProxyId[..2], $"{originalProxyId}.blob");
            var collected = await reopened.CollectUnreferencedAssetsAsync();
            Equal(true, collected.ToHashSet().SetEquals(
                    [sharedEdit.DerivedAssetId, binding.ProxyAssetId, firstOverride.Id, rollbackAsset.Id]),
                "collection removes every unreferenced attached asset");
            Equal(false, File.Exists(originalProxyPath), "collection deletes the replaced proxy blob");
            Equal(false, reopened.Content.Assets.Any(asset => asset.Id == binding.ProxyAssetId),
                "collection deletes the replaced proxy metadata");
            Equal(true, reopened.Content.Assets.Any(asset => asset.Id == uniqueEdit.DerivedAssetId),
                "collection protects a referenced asset's attached parent");
            Equal(true, reopened.Content.Assets.Any(asset => asset.Id == replacement.ProxyAssetId),
                "collection protects the active proxy metadata");
            reopened.RemoveInstancedCollisionProxy(tie.Id);
            Equal(false, reopened.IsAssetReferenced(replacement.ProxyAssetId),
                "removed proxy becomes eligible for safe collection");
            await reopened.SaveAsync();
            Equal(0, (await reopened.CollectUnreferencedAssetsAsync()).Count,
                "collection protects a proxy referenced by recovery");
            Equal(true, File.Exists(reopened.ResolveAttachedAssetPath(replacement.ProxyAssetId)),
                "recovery-protected proxy blob survives collection");
            await reopened.LoadRecoveryAsync(proxyRecovery.Id);
            EqualBinding(replacement, reopened.Content.InstancedCollisionBindings.Single(),
                "recovery restores the reusable proxy binding");
            await reopened.SaveAsync();

            var archivePath = Path.Combine(root, "portable-project.zip");
            ZipFile.CreateFromDirectory(movedPath, archivePath, CompressionLevel.Fastest, false);
            var transferredPath = Path.Combine(root, "transferred-project");
            ZipFile.ExtractToDirectory(archivePath, transferredPath);
            var transferred = await ForgeProjectWorkspace.OpenAsync(transferredPath);
            EqualBinding(replacement, transferred.Content.InstancedCollisionBindings.Single(),
                "zip transfer preserves the exact proxy binding");
            var transferredProxyPath = transferred.ResolveAttachedAssetPath(replacement.ProxyAssetId)
                ?? throw new InvalidOperationException("Transferred proxy blob did not resolve");
            var transferredProxyBytes = await File.ReadAllBytesAsync(transferredProxyPath);
            Equal(true, replacementBytes.SequenceEqual(transferredProxyBytes),
                "zip transfer preserves the project-relative proxy blob");
            var transferredTextureBytes = await File.ReadAllBytesAsync(
                transferred.ResolveAttachedAssetPath(texture.Id)
                ?? throw new InvalidOperationException("Transferred texture blob did not resolve"));
            Equal(true, textureBytes.SequenceEqual(transferredTextureBytes),
                "zip transfer preserves a parentless texture blob");
            var transferredTfragBytes = await File.ReadAllBytesAsync(
                transferred.ResolveAttachedAssetPath(tfrag.Id)
                ?? throw new InvalidOperationException("Transferred tfrag blob did not resolve"));
            Equal(true, tfragBytes.SequenceEqual(transferredTfragBytes),
                "zip transfer preserves a parentless tfrag blob");
            Equal(secondOverride.Id, transferred.ResolveAssetReference(sourceReference).Id,
                "zip transfer preserves the exact override binding");
            var transferredOverride = transferred.ResolveAsset(sourceReference, catalog)
                ?? throw new InvalidOperationException("Transferred override did not resolve");
            var transferredOverrideBytes = await File.ReadAllBytesAsync(transferredOverride.Path);
            Equal(true, secondOverrideBytes.SequenceEqual(transferredOverrideBytes),
                "zip transfer preserves the exact override blob");

            var repairedCatalog = await AssetCatalogStore.OpenAsync(Path.Combine(root, "repaired-catalog"));
            Equal(secondOverride.Id,
                transferred.ResolveAsset(sourceReference, repairedCatalog)!.Effective.Id,
                "portable override resolves without its vanilla source catalog");
            Equal(null, transferred.ResolveAssetPath(new(tie.Id, AssetKind.Tie), repairedCatalog),
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
            Equal(true, transferred.ResolveAssetPath(new(tie.Id, AssetKind.Tie), repairedCatalog) is not null,
                "repaired source TIE resolves after transfer");
            EqualBinding(replacement, transferred.Content.InstancedCollisionBindings.Single(),
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

    private static Task RestoreAsync(ForgeProjectWorkspace workspace, ForgeProjectState state)
    {
        workspace.RestoreState(state);
        return Task.CompletedTask;
    }

    private static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected {expected}, got {actual}");
    }

    private static void EqualBinding(
        ProjectInstancedCollisionBinding expected,
        ProjectInstancedCollisionBinding actual,
        string context) => Equal(
            true,
            expected.SourceAssetId == actual.SourceAssetId
                && expected.ProxyAssetId == actual.ProxyAssetId
                && expected.Recipe == actual.Recipe
                && expected.FaceTypeOverrides.SequenceEqual(actual.FaceTypeOverrides),
            context);

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }
}
