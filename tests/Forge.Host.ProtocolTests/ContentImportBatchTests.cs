using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Forge.Host.Domain;
using Forge.Host.Games.UYA;

internal static class ContentImportBatchTests
{
    public static async Task RunAsync()
    {
        VerifyChangedEntityIndex();
        var root = Path.Combine(Path.GetTempPath(), $"forge-content-import-batch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await VerifyAtomicCommandAsync(root);
            await VerifyRemovalAsync(root);
            await VerifySchemaPolicyAsync(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void VerifyChangedEntityIndex()
    {
        var unchanged = new ProjectEntity(
            EntityId.New(), "Unchanged", "ties", ProjectTransform.Identity, null, null);
        var modified = new ProjectEntity(
            EntityId.New(), "Before", "ties", ProjectTransform.Identity, null, null);
        var removed = new ProjectEntity(
            EntityId.New(), "Removed", "ties", ProjectTransform.Identity, null, null);
        var added = new ProjectEntity(
            EntityId.New(), "Added", "ties", ProjectTransform.Identity, null, null);

        Equal(true,
            new[] { modified.EntityId, removed.EntityId, added.EntityId }.SequenceEqual(
                EditorRuntime.ChangedEntityIds(
                [unchanged, modified, removed],
                [unchanged, modified with { Name = "After" }, added])),
            "content-import change index preserves changed, removed, then added ordering");
    }

    private static async Task VerifyRemovalAsync(string root)
    {
        var projectPath = Path.Combine(root, "removal-project");
        var workspace = await CreateProjectAsync(projectPath);
        var bytes = "removable donor TIE"u8.ToArray();
        var assetId = AssetId.Compute(AssetKind.Tie, 1, bytes);
        var entityId = EntityId.New();
        var groupId = GroupId.New();
        var batch = Batch("import-44444444444444444444444444444444", assetId, entityId, groupId);
        await using var runtime = new EditorRuntime();
        await runtime.OpenAsync(projectPath, TimeSpan.FromDays(1));
        var command = await runtime.ReadWorkspaceAsync(projectPath, (active, _) => Task.FromResult(
            new ContentImportBatchCommit(
                active.CurrentFingerprint,
                batch,
                ImportedContent(active.Content, assetId, entityId, groupId),
                [new(AssetKind.Tie, 1, bytes)])));
        await runtime.CommitContentImportAsync(command);

        var externalGroup = await runtime.ReadWorkspaceAsync(projectPath, (active, _) =>
        {
            var group = active.CreateGroup("External dependency");
            active.AddGroupMembers(group.GroupId, [entityId]);
            return Task.FromResult(group.GroupId);
        });
        var protectedPlan = await runtime.ReadWorkspaceAsync(projectPath, (active, token) => Task.FromResult(
            UyaContentImportRemovalService.Prepare(active.CreateProjection(), batch.BatchId, token).Plan));
        Equal(false, protectedPlan.Delete.Contains($"entity:{entityId}"),
            "external group retains imported entity");
        Equal(true, protectedPlan.Survivors.Any(value => value.StableId == $"entity:{entityId}"
                && value.Reason.Contains("group", StringComparison.OrdinalIgnoreCase)),
            "removal preview explains external dependency");
        Equal(true, protectedPlan.Survivors.Any(value => value.StableId == $"asset:{assetId}"),
            "dependency-retained entity preserves its shared asset");
        await runtime.ReadWorkspaceAsync<object?>(projectPath, (active, _) =>
        {
            active.DeleteGroup(externalGroup);
            return Task.FromResult<object?>(null);
        });

        var plan = await runtime.ReadWorkspaceAsync(projectPath, (active, token) => Task.FromResult(
            UyaContentImportRemovalService.Prepare(active.CreateProjection(), batch.BatchId, token).Plan));
        Equal(true, plan.Delete.Contains($"entity:{entityId}"), "removal previews imported entity");
        Equal(true, plan.Delete.Contains($"group:{groupId}"), "removal previews imported group");
        Equal(true, plan.Delete.Contains($"asset:{assetId}"), "removal previews unreferenced asset");
        Equal(true, plan.Delete.Contains("class:tie:16384"), "removal previews unused class allocation");

        await runtime.ExecuteAsync(new(
            Guid.NewGuid().ToString("D"), EditorCommandKind.RenameProject, [], Text: "Changed target"));
        await ThrowsAsync<InvalidOperationException>(() => UyaContentImportRemovalService.ApplyAsync(
            runtime, projectPath, batch.BatchId, plan.TargetFingerprint));
        await runtime.ExecuteAsync(HistoryCommand(EditorCommandKind.Undo));

        plan = await runtime.ReadWorkspaceAsync(projectPath, (active, token) => Task.FromResult(
            UyaContentImportRemovalService.Prepare(active.CreateProjection(), batch.BatchId, token).Plan));
        var snapshot = await UyaContentImportRemovalService.ApplyAsync(
            runtime, projectPath, batch.BatchId, plan.TargetFingerprint);
        Equal(0, snapshot.Entities.Count, "removal deletes imported entity");
        Equal(0, snapshot.Groups.Count, "removal deletes imported group");
        Equal(1, snapshot.ImportBatches.Count, "removal preserves append-only provenance");
        await runtime.ReadWorkspaceAsync<object?>(projectPath, (active, _) =>
        {
            Equal(0, active.Content.Assets.Count, "removal drops unreferenced asset metadata");
            Equal(0, active.Content.CustomClasses.Allocations.Count, "removal drops unused class allocation");
            return Task.FromResult<object?>(null);
        });

        snapshot = await runtime.ExecuteAsync(HistoryCommand(EditorCommandKind.Undo));
        Equal(1, snapshot.Entities.Count, "removal undo restores imported entity");
        snapshot = await runtime.ExecuteAsync(HistoryCommand(EditorCommandKind.Redo));
        Equal(0, snapshot.Entities.Count, "removal redo deletes imported entity again");
        Equal(true, File.Exists(BlobPath(projectPath, assetId)), "removal retains blob for undo and later cleanup");

        var replacementPath = Path.Combine(root, "replacement-removal-project");
        var replacementWorkspace = await CreateProjectAsync(replacementPath);
        var replacementEntity = EntityId.New();
        var replacementBatch = Batch(
            "import-55555555555555555555555555555555", assetId, replacementEntity) with
        {
            Families = [new("ties", ContentImportMode.Replace)],
        };
        await replacementWorkspace.CommitContentImportAsync(new(
            replacementWorkspace.CurrentFingerprint,
            replacementBatch,
            ImportedContent(replacementWorkspace.Content, assetId, replacementEntity, GroupId.New()) with
            {
                Groups = [],
            },
            [new(AssetKind.Tie, 1, bytes)]));
        var replacementPlan = UyaContentImportRemovalService.Prepare(
            replacementWorkspace.CreateProjection(), replacementBatch.BatchId).Plan;
        Equal(false, replacementPlan.Delete.Contains($"entity:{replacementEntity}"),
            "replace import retains current replacement");
        Equal(true, replacementPlan.Warnings.Any(value => value.Contains("cannot restore", StringComparison.Ordinal)),
            "replace removal explains irreversibility");
    }

    private static async Task VerifyAtomicCommandAsync(string root)
    {
        var projectPath = Path.Combine(root, "project");
        var workspace = await CreateProjectAsync(projectPath);
        var bytes = "verified donor TIE"u8.ToArray();
        var assetId = AssetId.Compute(AssetKind.Tie, 1, bytes);
        var entityId = EntityId.New();
        var groupId = GroupId.New();
        var nextContent = ImportedContent(workspace.Content, assetId, entityId, groupId);
        var batch = Batch("import-11111111111111111111111111111111", assetId, entityId, groupId);
        VerifyBatchComposition(workspace, batch);

        await ThrowsAsync<InvalidOperationException>(() => workspace.CommitContentImportAsync(new(
            new string('0', 64), batch, nextContent, [new(AssetKind.Tie, 1, bytes)])));
        Equal(0, workspace.Content.ImportBatches.Count, "stale plan exposes no batch");

        var otherBytes = "cyclic donor parent"u8.ToArray();
        var otherId = AssetId.Compute(AssetKind.Tie, 1, otherBytes);
        await ThrowsAsync<InvalidDataException>(() => workspace.CommitContentImportAsync(new(
            workspace.CurrentFingerprint,
            batch,
            workspace.Content,
            [new(AssetKind.Tie, 1, bytes, otherId), new(AssetKind.Tie, 1, otherBytes, assetId)])));
        Equal(false, File.Exists(BlobPath(projectPath, assetId)), "invalid parent graph stages no blob");

        foreach (var phase in Enum.GetValues<ContentImportCommitPhase>())
        {
            var before = workspace.CurrentFingerprint;
            await ThrowsAsync<InjectedFailureException>(() => workspace.CommitContentImportAsync(new(
                before,
                batch,
                nextContent,
                [new(AssetKind.Tie, 1, bytes)],
                current =>
                {
                    if (current == phase) throw new InjectedFailureException();
                })));
            Equal(before, workspace.CurrentFingerprint, $"{phase} fault preserves project state");
            Equal(0, workspace.Content.ImportBatches.Count, $"{phase} fault exposes no batch");
            Equal(0, workspace.Content.Assets.Count, $"{phase} fault exposes no asset");
            Equal(false, File.Exists(BlobPath(projectPath, assetId)), $"{phase} fault removes staged blob");
        }

        using (var cancellation = new CancellationTokenSource())
        {
            await ThrowsAsync<OperationCanceledException>(() => workspace.CommitContentImportAsync(new(
                workspace.CurrentFingerprint,
                batch,
                nextContent,
                [new(AssetKind.Tie, 1, bytes)],
                phase =>
                {
                    if (phase == ContentImportCommitPhase.BlobsStaged) cancellation.Cancel();
                }), cancellation.Token));
            Equal(0, workspace.Content.ImportBatches.Count, "cancellation exposes no batch");
            Equal(false, File.Exists(BlobPath(projectPath, assetId)), "cancellation removes staged blob");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(BlobPath(projectPath, assetId))!);
        await File.WriteAllBytesAsync(BlobPath(projectPath, assetId), "corrupt"u8.ToArray());
        await ThrowsAsync<InvalidDataException>(() => workspace.CommitContentImportAsync(new(
            workspace.CurrentFingerprint, batch, nextContent, [new(AssetKind.Tie, 1, bytes)])));
        Equal(0, workspace.Content.ImportBatches.Count, "integrity failure exposes no batch");
        File.Delete(BlobPath(projectPath, assetId));

        await using var runtime = new EditorRuntime();
        await runtime.OpenAsync(projectPath, TimeSpan.FromDays(1));
        var command = await runtime.ReadWorkspaceAsync(projectPath, (active, _) => Task.FromResult(new ContentImportBatchCommit(
            active.CurrentFingerprint,
            batch,
            ImportedContent(active.Content, assetId, entityId, groupId),
            [new(AssetKind.Tie, 1, bytes)])));
        var snapshot = await runtime.CommitContentImportAsync(command);
        Equal(true, snapshot.CanUndo, "import batch is one undoable command");
        var origin = snapshot.Entities.Single(value => value.EntityId == entityId).Origin!;
        Equal(EditorContentOriginKind.Imported, origin.Kind, "imported entity exposes its origin");
        Equal(batch.BatchId, origin.ImportBatchId, "imported entity exposes its batch ID");
        Equal(batch.SourceLevel, origin.SourceLevel, "imported entity exposes its donor level");
        var groupOrigin = snapshot.Groups.Single(value => value.GroupId == groupId).Origin!;
        Equal(EditorContentOriginKind.Imported, groupOrigin.Kind, "imported group exposes its origin");
        Equal(batch.BatchId, groupOrigin.ImportBatchId, "imported group exposes its batch ID");
        Equal(batch.SourceLevel, groupOrigin.SourceLevel, "imported group exposes its donor level");
        var report = snapshot.ImportBatches.Single();
        Equal(batch.BatchId, report.BatchId, "import report exposes its batch ID");
        Equal(batch.SourceLevel, report.SourceLevel, "import report exposes its donor level");
        Equal(batch.Added.Count, report.AddedCount, "import report exposes added total");
        Equal(ContentImportMode.Add, report.Families.Single().Mode, "import report exposes family mode");
        await VerifyImportedStateAsync(runtime, projectPath, 1, 1, true, "committed import");

        snapshot = await runtime.ExecuteAsync(HistoryCommand(EditorCommandKind.Undo));
        Equal(true, snapshot.CanRedo, "import undo enables redo");
        await VerifyImportedStateAsync(runtime, projectPath, 0, 0, false, "undone import");
        Equal(true, File.Exists(BlobPath(projectPath, assetId)), "undo retains deduplicated blob");

        await runtime.ExecuteAsync(HistoryCommand(EditorCommandKind.Redo));
        await VerifyImportedStateAsync(runtime, projectPath, 1, 1, true, "redone import");

        var secondBatch = batch with
        {
            BatchId = "import-22222222222222222222222222222222",
            Added = [],
            Reused = [$"asset:{assetId}"],
        };
        var second = await runtime.ReadWorkspaceAsync(projectPath, (active, _) => Task.FromResult(new ContentImportBatchCommit(
            active.CurrentFingerprint,
            secondBatch,
            active.Content,
            [new(AssetKind.Tie, 1, bytes)])));
        await runtime.CommitContentImportAsync(second);
        await VerifyImportedStateAsync(runtime, projectPath, 2, 1, true, "deduplicated import");
        Equal(1, Directory.EnumerateFiles(Path.Combine(projectPath, "assets"), "*.blob", SearchOption.AllDirectories).Count(),
            "deduplicated import stores one blob");

        var recovery = await runtime.ReadWorkspaceAsync(projectPath, (active, token) => active.WriteRecoveryAsync(token));
        Equal(true, recovery is not null, "import batch writes recovery snapshot");
        var recovered = await ForgeProjectWorkspace.OpenAsync(projectPath);
        await recovered.LoadRecoveryAsync(recovery!.Id);
        Equal(2, recovered.Content.ImportBatches.Count, "recovery restores complete batch history");
        Equal(1, recovered.Content.CustomClasses.Allocations.Count, "recovery restores custom allocation");
        Equal(true, recovered.Content.Entities.Any(value => value.EntityId == entityId),
            "recovery restores imported entity");

        await runtime.SaveAsync();
        var archivePath = Path.Combine(root, "project.zip");
        var transferredPath = Path.Combine(root, "transferred");
        ZipFile.CreateFromDirectory(projectPath, archivePath, CompressionLevel.Fastest, includeBaseDirectory: false);
        ZipFile.ExtractToDirectory(archivePath, transferredPath);
        var transferred = await ForgeProjectWorkspace.OpenAsync(transferredPath);
        Equal(2, transferred.Content.ImportBatches.Count, "ZIP round trip preserves batch provenance");
        Equal(1, transferred.Content.Assets.Count, "ZIP round trip preserves attached metadata");
        Equal(true, File.Exists(BlobPath(transferredPath, assetId)), "ZIP round trip preserves attached blob");
    }

    private static async Task VerifySchemaPolicyAsync(string root)
    {
        var projectPath = Path.Combine(root, "schema-project");
        var workspace = await CreateProjectAsync(projectPath);
        var manifestPath = Path.Combine(projectPath, ForgeProjectWorkspace.ManifestFileName);
        var manifest = JsonNode.Parse(await File.ReadAllBytesAsync(manifestPath))!.AsObject();
        var content = JsonNode.Parse(Decompress(await File.ReadAllBytesAsync(workspace.ContentFilePath)))!.AsObject();
        manifest["schemaVersion"] = ProjectSchema.CurrentVersion - 1;
        content["schemaVersion"] = ProjectSchema.CurrentVersion - 1;
        content.Remove("importBatches");
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
        await File.WriteAllBytesAsync(workspace.ContentFilePath, Compress(Encoding.UTF8.GetBytes(content.ToJsonString())));
        var migrated = await ForgeProjectWorkspace.OpenAsync(projectPath);
        Equal(0, migrated.Content.ImportBatches.Count, "older schema gains an empty batch ledger");

        await migrated.SaveAsync();
        content = JsonNode.Parse(Decompress(await File.ReadAllBytesAsync(migrated.ContentFilePath)))!.AsObject();
        var assetId = AssetId.Compute(AssetKind.Tie, 1, "schema batch"u8);
        var futureBatch = JsonNode.Parse(ForgeProjectPersistence.Serialize(
            Batch("import-33333333333333333333333333333333", assetId, EntityId.New())))!.AsObject();
        futureBatch["futureField"] = true;
        content["importBatches"] = new JsonArray(futureBatch);
        await File.WriteAllBytesAsync(migrated.ContentFilePath, Compress(Encoding.UTF8.GetBytes(content.ToJsonString())));
        await ThrowsAsync<JsonException>(() => ForgeProjectWorkspace.OpenAsync(projectPath));

        futureBatch.Remove("futureField");
        futureBatch["schemaVersion"] = ContentImportBatchSchema.CurrentVersion + 1;
        await File.WriteAllBytesAsync(migrated.ContentFilePath, Compress(Encoding.UTF8.GetBytes(content.ToJsonString())));
        await ThrowsAsync<InvalidDataException>(() => ForgeProjectWorkspace.OpenAsync(projectPath));
    }

    private static async Task VerifyImportedStateAsync(
        EditorRuntime runtime,
        string projectPath,
        int batchCount,
        int assetCount,
        bool imported,
        string description) => await runtime.ReadWorkspaceAsync<object?>(projectPath, (workspace, _) =>
    {
        Equal(batchCount, workspace.Content.ImportBatches.Count, $"{description} batch count");
        Equal(assetCount, workspace.Content.Assets.Count, $"{description} asset count");
        Equal(imported, workspace.Content.CustomClasses.Allocations.Count == 1,
            $"{description} allocation state");
        Equal(imported, workspace.Content.Entities.Count == 1, $"{description} entity state");
        return Task.FromResult<object?>(null);
    });

    private static ForgeProjectContent ImportedContent(
        ForgeProjectContent content,
        AssetId assetId,
        EntityId entityId,
        GroupId groupId)
    {
        var reference = new ProjectAssetReference(assetId, AssetKind.Tie);
        return content with
        {
            Entities =
            [
                new(entityId, "Imported donor TIE", "ties", ProjectTransform.Identity, reference,
                    new("UYA", 41, "ties", 7)),
            ],
            Groups = [new(groupId, "Imported donor group", [entityId])],
            CustomClasses = content.CustomClasses with
            {
                Allocations =
                [
                    new(reference, 0x4000,
                        new(ProjectCustomClassOrigin.DonorMap, SourceClassId: 7)),
                ],
            },
        };
    }

    private static ContentImportBatch Batch(
        string batchId,
        AssetId assetId,
        EntityId entityId,
        GroupId? groupId = null) => new(
        ContentImportBatchSchema.CurrentVersion,
        batchId,
        ContentImportPlanSchema.CurrentVersion,
        ContentImportCapabilitySchema.CurrentVersion,
        1_800_000_000_000,
        "UYA",
        "NTSC-U",
        "1.00",
        new string('a', 32),
        new string('b', 64),
        41,
        [new("ties", ContentImportMode.Add)],
        new[] { $"asset:{assetId}", "class:tie:16384", $"entity:{entityId}" }
            .Concat(groupId is { } value ? [$"group:{value}"] : [])
            .Order(StringComparer.Ordinal).ToArray(),
        [],
        [],
        []);

    private static void VerifyBatchComposition(ForgeProjectWorkspace workspace, ContentImportBatch firstBatch)
    {
        var secondBatch = firstBatch with
        {
            Families = [new("cuboids", ContentImportMode.Replace)],
            Added = ["entity:cuboid"],
            Reused = ["asset:shared"],
            Replaced = ["entity:old-cuboid"],
            Protected = ["entity:protected-cuboid"],
        };
        var plan = new ContentImportPlan(
            ContentImportPlanSchema.CurrentVersion,
            ContentImportCapabilitySchema.CurrentVersion,
            firstBatch.BatchId,
            "UYA",
            "NTSC-U",
            "1.00",
            firstBatch.SourceFingerprint,
            firstBatch.SourceLevelHash,
            firstBatch.SourceLevel,
            workspace.Manifest.ProjectId.ToString(),
            workspace.CurrentFingerprint,
            workspace.Manifest.BaseLevel.Level,
            0,
            [Family("ties", ContentImportMode.Add), Family("cuboids", ContentImportMode.Replace)],
            [],
            []);
        var commands = new[]
        {
            new ContentImportBatchCommit(workspace.CurrentFingerprint, firstBatch, workspace.Content, []),
            new ContentImportBatchCommit(workspace.CurrentFingerprint, secondBatch, workspace.Content, []),
        };
        var composed = UyaContentImportApplyService.Compose(plan, workspace.Content, commands, 1234);
        Equal(workspace.CurrentFingerprint, composed.ExpectedTargetFingerprint,
            "composed import retains approved fingerprint");
        Equal(2, composed.Batch.Families.Count, "composed import retains selected families");
        Equal(true, composed.Batch.Families.Select(value => value.Family)
                .SequenceEqual(["cuboids", "ties"]),
            "composed import sorts family modes for project validation");
        Equal(true, composed.Batch.Added.Contains("entity:cuboid"), "composed import merges additions");
        Equal(true, composed.Batch.Reused.Contains("asset:shared"), "composed import merges reuse");
        Equal(true, composed.Batch.Replaced.Contains("entity:old-cuboid"), "composed import merges removals");
        Equal(true, composed.Batch.Protected.Contains("entity:protected-cuboid"),
            "composed import merges protected survivors");
    }

    private static ContentImportFamilyPlan Family(string family, ContentImportMode mode) => new(
        family, family, ContentImportMode.AddAndReplace,
        mode, ContentImportSourceState.Present, true, false, true,
        [], [], [], [], [], [], [], 0, [], []);

    private static async Task<ForgeProjectWorkspace> CreateProjectAsync(string path) =>
        await ForgeProjectWorkspace.CreateAsync(
            path,
            "Import target",
            new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
            new("UYA", "NTSC-U", "1.00", 40, new string('c', 32)),
            []);

    private static EditorCommand HistoryCommand(EditorCommandKind kind) =>
        new(Guid.NewGuid().ToString("D"), kind, []);

    private static string BlobPath(string projectPath, AssetId id)
    {
        var value = id.ToString();
        return Path.Combine(projectPath, "assets", value[..2], $"{value}.blob");
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

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try
        {
            await action();
            throw new InvalidOperationException($"Expected {typeof(T).Name}.");
        }
        catch (T)
        {
        }
    }

    private static void Equal<T>(T expected, T actual, string name)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{name}: expected {expected}, got {actual}");
    }

    private sealed class InjectedFailureException : Exception;
}
