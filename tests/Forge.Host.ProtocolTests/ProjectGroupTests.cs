using System.IO.Compression;
using Forge.Host.Domain;
using Forge.Host.Games.UYA;

internal static class ProjectGroupTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-project-groups-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var projectPath = Path.Combine(root, "project");
            var firstId = EntityId.New();
            var secondId = EntityId.New();
            var thirdId = EntityId.New();
            var target = new ProjectTargetProfile("UYA", "NTSC-U", "1.00", "uya-ntsc-u");
            var baseLevel = new ProjectBaseLevel("UYA", "NTSC-U", "1.00", 40, UyaIsoService.SupportedMd5);
            var project = await ForgeProjectWorkspace.CreateAsync(
                projectPath,
                "Groups",
                target,
                baseLevel,
                [Entity(firstId, "First"), Entity(secondId, "Second"), Entity(thirdId, "Third")]);

            var beforeInput = await UyaHudBakeService.CreateBakeInputAsync(projectPath);
            var beforePlan = BakeLayerGraph.CreatePlan(new(target, "translator-1", "baker-1"), [beforeInput]);
            var staging = await BakeStagingStore.OpenAsync(projectPath);
            var beforeOutput = await UyaHudBakeService.StageAsync(
                projectPath, staging, beforePlan.Layers.Single(value => value.Layer == BakeLayerId.Hud));

            var firstGroup = project.CreateGroup("First group");
            project.AddGroupMembers(firstGroup.GroupId, [firstId, secondId]);
            var secondGroup = project.CreateGroup("Second group");
            project.AddGroupMembers(secondGroup.GroupId, [secondId]);
            Equal(true, project.Content.Groups.All(group => group.Members.Contains(secondId)),
                "entities can belong to multiple groups");
            var beforeRejectedMutation = project.CurrentFingerprint;
            Throws<InvalidOperationException>(() => project.AddGroupMembers(firstGroup.GroupId, [secondId]));
            Equal(beforeRejectedMutation, project.CurrentFingerprint, "duplicate membership rejection is atomic");
            project.ReorderGroup(secondGroup.GroupId, 0);
            project.RenameGroup(secondGroup.GroupId, "Reordered group");
            project.RemoveGroupMembers(firstGroup.GroupId, [firstId]);
            Equal(secondGroup.GroupId, project.Content.Groups[0].GroupId, "group order persists explicitly");
            Equal([secondId], project.Content.Groups[1].Members, "member removal preserves remaining order");
            await project.SaveAsync();

            var afterInput = await UyaHudBakeService.CreateBakeInputAsync(projectPath);
            var afterPlan = BakeLayerGraph.CreatePlan(new(target, "translator-1", "baker-1"), [afterInput]);
            var afterOutput = await UyaHudBakeService.StageAsync(
                projectPath, staging, afterPlan.Layers.Single(value => value.Layer == BakeLayerId.Hud));
            var beforeHudPlan = beforePlan.Layers.Single(value => value.Layer == BakeLayerId.Hud);
            var afterHudPlan = afterPlan.Layers.Single(value => value.Layer == BakeLayerId.Hud);
            Equal(beforeHudPlan.ContentFingerprint, afterHudPlan.ContentFingerprint,
                "group-only edits do not change bake content fingerprints");
            Equal(beforeHudPlan.InputFingerprint, afterHudPlan.InputFingerprint,
                "group-only edits do not change bake input fingerprints");
            Equal(beforeOutput.OutputFingerprint, afterOutput.OutputFingerprint,
                "group-only edits do not change staged output");

            await using (var runtime = new EditorRuntime())
            {
                var snapshot = await runtime.OpenAsync(projectPath, TimeSpan.FromMilliseconds(10));
                Equal(true, snapshot.Groups.Select(group => group.GroupId)
                    .SequenceEqual([secondGroup.GroupId, firstGroup.GroupId]), "snapshot preserves group order");
                Equal(true, runtime.HasCapability("editor.group.membership"), "group capability advertised");

                snapshot = await runtime.ExecuteAsync(new(
                    CommandId(), EditorCommandKind.CreateGroup, [], Text: "Runtime group", GroupEdit: new()));
                var runtimeGroup = snapshot.Groups.Single(group => group.Name == "Runtime group");
                snapshot = await runtime.ExecuteAsync(Command(EditorCommandKind.Undo));
                Equal(false, snapshot.Groups.Any(group => group.GroupId == runtimeGroup.GroupId),
                    "group creation is undoable");
                snapshot = await runtime.ExecuteAsync(Command(EditorCommandKind.Redo));
                Equal(runtimeGroup.GroupId, snapshot.Groups.Single(group => group.Name == "Runtime group").GroupId,
                    "group redo preserves stable ID");

                snapshot = await runtime.ExecuteAsync(new(
                    CommandId(), EditorCommandKind.RenameGroup, [], Text: "Runtime renamed",
                    GroupEdit: new(runtimeGroup.GroupId)));
                snapshot = await runtime.ExecuteAsync(new(
                    CommandId(), EditorCommandKind.AddGroupMembers, [firstId, thirdId],
                    GroupEdit: new(runtimeGroup.GroupId)));
                Equal([firstId, thirdId], snapshot.Groups.Single(group => group.GroupId == runtimeGroup.GroupId).Members,
                    "membership command preserves requested order");
                var rejected = snapshot.Groups.Single(group => group.GroupId == runtimeGroup.GroupId);
                await ThrowsAsync<InvalidOperationException>(() => runtime.ExecuteAsync(new(
                    CommandId(), EditorCommandKind.AddGroupMembers, [firstId],
                    GroupEdit: new(runtimeGroup.GroupId))));
                snapshot = await runtime.GetSnapshotAsync();
                Equal(rejected, snapshot.Groups.Single(group => group.GroupId == runtimeGroup.GroupId),
                    "failed membership command preserves snapshot state");

                snapshot = await runtime.ExecuteAsync(new(
                    CommandId(), EditorCommandKind.RemoveGroupMembers, [firstId],
                    GroupEdit: new(runtimeGroup.GroupId)));
                Equal([thirdId], snapshot.Groups.Single(group => group.GroupId == runtimeGroup.GroupId).Members,
                    "membership removal updates the group");
                await WaitForAsync(async () => (await runtime.ReadEventsAsync(0, 1_024))
                    .Any(value => value.Kind == EditorEventKind.RecoveryWritten));
                Equal(true, (await project.ListRecoveriesAsync()).Count > 0, "group mutations write recovery state");
                snapshot = await runtime.SaveAsync();
                Equal(false, snapshot.IsDirty, "saved groups are clean");

                var entityCount = snapshot.Entities.Count;
                snapshot = await runtime.ExecuteAsync(new(
                    CommandId(), EditorCommandKind.DeleteGroup, [], GroupEdit: new(runtimeGroup.GroupId)));
                Equal(entityCount, snapshot.Entities.Count, "deleting a group never deletes entities");
                snapshot = await runtime.ExecuteAsync(Command(EditorCommandKind.Undo));
                Equal(true, snapshot.Groups.Any(group => group.GroupId == runtimeGroup.GroupId),
                    "group deletion is undoable");
                snapshot = await runtime.ExecuteAsync(new(
                    CommandId(), EditorCommandKind.DeleteEntities, [thirdId]));
                Equal(false, snapshot.Groups.Single(group => group.GroupId == runtimeGroup.GroupId)
                    .Members.Contains(thirdId), "entity deletion removes group membership atomically");
                await runtime.ExecuteAsync(Command(EditorCommandKind.Undo));
                await runtime.SaveAsync();
            }

            project = await ForgeProjectWorkspace.OpenAsync(projectPath);
            var savedFingerprint = project.CurrentFingerprint;
            var archivePath = Path.Combine(root, "groups.zip");
            ZipFile.CreateFromDirectory(projectPath, archivePath, CompressionLevel.Fastest, false);
            var transferredPath = Path.Combine(root, "transferred");
            ZipFile.ExtractToDirectory(archivePath, transferredPath);
            var transferred = await ForgeProjectWorkspace.OpenAsync(transferredPath);
            Equal(savedFingerprint, transferred.CurrentFingerprint, "ZIP transfer preserves group state");
            Equal(true, project.Content.Groups.Zip(transferred.Content.Groups).All(pair =>
                    pair.First.GroupId == pair.Second.GroupId
                    && pair.First.Name == pair.Second.Name
                    && pair.First.Members.SequenceEqual(pair.Second.Members)),
                "ZIP transfer preserves IDs, order, and memberships");

            var missingId = EntityId.New();
            var diagnosedGroup = transferred.Content.Groups[0] with
            {
                Members = [.. transferred.Content.Groups[0].Members, missingId],
            };
            await ForgeProjectPersistence.SaveAsync(
                transferredPath,
                transferred.Manifest,
                transferred.Content with
                {
                    Groups = [diagnosedGroup, .. transferred.Content.Groups.Skip(1)],
                });
            await using var diagnosedRuntime = new EditorRuntime();
            var diagnosed = await diagnosedRuntime.OpenAsync(transferredPath, TimeSpan.Zero);
            Equal([missingId], diagnosed.Groups[0].MissingMembers,
                "missing group members remain visible in the snapshot");
            Equal(true, diagnosed.Diagnostics.Any(value => value.Code == "group.missing-member"
                && value.Message.Contains(missingId.ToString(), StringComparison.Ordinal)),
                "missing group members produce actionable diagnostics");
            diagnosed = await diagnosedRuntime.ExecuteAsync(new(
                CommandId(), EditorCommandKind.RemoveGroupMembers, [missingId],
                GroupEdit: new(diagnosedGroup.GroupId)));
            Equal(0, diagnosed.Groups[0].MissingMembers.Count,
                "missing membership can be removed without resolving the entity");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static ProjectEntity Entity(EntityId id, string name) =>
        new(id, name, "entities", ProjectTransform.Identity, null);

    private static string CommandId() => Guid.NewGuid().ToString("D");

    private static EditorCommand Command(EditorCommandKind kind) => new(CommandId(), kind, []);

    private static async Task WaitForAsync(Func<Task<bool>> predicate)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (await predicate()) return;
            await Task.Delay(10);
        }
        throw new InvalidOperationException("Timed out waiting for group recovery state.");
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Equal<T>(T expected, T actual, string context)
    {
        if (expected is System.Collections.IEnumerable expectedItems
            && actual is System.Collections.IEnumerable actualItems
            && expected is not string && actual is not string)
        {
            var expectedValues = expectedItems.Cast<object?>().ToArray();
            var actualValues = actualItems.Cast<object?>().ToArray();
            if (expectedValues.SequenceEqual(actualValues)) return;
        }
        else if (EqualityComparer<T>.Default.Equals(expected, actual))
        {
            return;
        }
        throw new InvalidOperationException($"{context}: expected {expected}, got {actual}");
    }
}
