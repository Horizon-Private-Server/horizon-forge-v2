using Forge.Host.Games.UYA;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Forge.Host.Domain;

internal static class EditorRuntimeTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-editor-runtime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await using var runtime = new EditorRuntime();
        try
        {
            var firstId = EntityId.New();
            var secondId = EntityId.New();
            var target = new ProjectTargetProfile("UYA", "NTSC-U", "1.00", "uya-ntsc-u");
            Equal((byte)0x0f, new EditorTieCollisionGenerationSettings().RawType,
                "TIE collision defaults to walkable collision ID");
            Equal(-1, new EditorTieCollisionGenerationSettings().SurfaceLodIndex,
                "TIE collision defaults to automatic surface LOD selection");
            var baseLevel = new ProjectBaseLevel(
                "UYA", "NTSC-U", "1.00", 3, UyaIsoService.SupportedMd5,
                EntityVersion: ProjectSchema.CurrentBaseEntityVersion);
            var firstPath = Path.Combine(root, "first");
            var secondPath = Path.Combine(root, "second");
            await ForgeProjectWorkspace.CreateAsync(firstPath, "First", target, baseLevel,
                [Entity(firstId, "One"), Entity(secondId, "Two")]);
            await ForgeProjectWorkspace.CreateAsync(secondPath, "Second", target, baseLevel,
                [Entity(EntityId.New(), "Other")]);
            await VerifyAssetPlacementHistoryAsync(root, target, baseLevel);
            await VerifySkyShellHistoryAsync(root, target, baseLevel);
            await VerifyUyaSkyShellCommandsAsync(root, target, baseLevel);
            await VerifyCollisionHistoryAsync(root, target, baseLevel);
            await VerifyTieCollisionHistoryAsync(root, target, baseLevel);
            await VerifyTieCollisionPreviewAsync(root, target, baseLevel);

            var snapshot = await runtime.OpenAsync(firstPath, TimeSpan.FromMilliseconds(25));
            Equal(false, snapshot.IsDirty, "opened runtime clean state");
            Equal(true, runtime.HasCapability("editor.transform.update"), "runtime capability");
            Equal(true, snapshot.Tools.Select(tool => tool.Id).SequenceEqual(["select", "translate", "rotate", "scale"]),
                "registered transform tools");

            var selection = Command(EditorCommandKind.SetSelection, [firstId, secondId]);
            var serialized = JsonSerializer.Serialize(selection);
            var roundTrip = JsonSerializer.Deserialize<EditorCommand>(serialized)
                ?? throw new InvalidOperationException("Editor command did not deserialize");
            Equal(selection.Id, roundTrip.Id, "serializable command ID");
            Equal(true, selection.EntityIds.SequenceEqual(roundTrip.EntityIds), "serializable command entities");
            snapshot = await runtime.ExecuteAsync(roundTrip);
            Equal(true, snapshot.Selection.SequenceEqual([firstId, secondId]), "shared Entity-ID selection");

            var sequenceBeforeFailure = snapshot.LastEventSequence;
            await ThrowsAsync<ArgumentException>(() => runtime.ExecuteAsync(
                Command(EditorCommandKind.SetSelection, [EntityId.New()])));
            snapshot = await runtime.GetSnapshotAsync();
            Equal(sequenceBeforeFailure, snapshot.LastEventSequence, "invalid command emits no event");
            Equal(true, snapshot.Selection.SequenceEqual([firstId, secondId]), "invalid command changes no selection");

            var transform = ProjectTransform.Identity with { Position = new(10, 20, 30) };
            snapshot = await runtime.ExecuteAsync(Command(EditorCommandKind.UpdateTransform, [firstId], transform));
            Equal(true, snapshot.IsDirty, "mutation marks runtime dirty");
            Equal(new ProjectVector3(10, 20, 30),
                snapshot.Entities.Single(entity => entity.EntityId == firstId).Transform.Position,
                "runtime transform mutation");
            Equal(true, snapshot.CanUndo, "mutation enables undo");
            snapshot = await runtime.ExecuteAsync(Command(EditorCommandKind.Undo, []));
            Equal(ProjectTransform.Identity.Position,
                snapshot.Entities.Single(entity => entity.EntityId == firstId).Transform.Position,
                "undo restores transform");
            Equal(false, snapshot.IsDirty, "undo restores clean state");
            Equal(true, snapshot.CanRedo, "undo enables redo");
            snapshot = await runtime.ExecuteAsync(Command(EditorCommandKind.Redo, []));
            Equal(transform.Position,
                snapshot.Entities.Single(entity => entity.EntityId == firstId).Transform.Position,
                "redo restores transform mutation");
            Equal(false, snapshot.CanRedo, "redo consumes redo entry");
            var levelSettings = new ProjectLevelSettings(new(1, 2, 3), new(4, 5, 6), 10, 175, 255, 0);
            snapshot = await runtime.ExecuteAsync(new(
                Guid.NewGuid().ToString("D"), EditorCommandKind.UpdateLevelSettings, [], LevelSettings: levelSettings));
            Equal(levelSettings, snapshot.LevelSettings, "runtime level settings mutation");
            snapshot = await runtime.ExecuteAsync(Command(EditorCommandKind.Undo, []));
            Equal(null, snapshot.LevelSettings, "undo restores level settings");
            snapshot = await runtime.ExecuteAsync(Command(EditorCommandKind.Redo, []));
            Equal(levelSettings, snapshot.LevelSettings, "redo restores level settings mutation");
            var secondTransform = ProjectTransform.Identity with { Position = new(40, 50, 60) };
            snapshot = await runtime.ExecuteAsync(new(
                Guid.NewGuid().ToString("D"), EditorCommandKind.UpdateTransforms, [firstId, secondId],
                Transforms: [new(firstId, transform), new(secondId, secondTransform)]));
            Equal(secondTransform.Position,
                snapshot.Entities.Single(entity => entity.EntityId == secondId).Transform.Position,
                "runtime batch transform mutation");
            snapshot = await runtime.ExecuteAsync(Command(EditorCommandKind.CopyEntities, [firstId]));
            Equal(true, snapshot.CanPaste, "copy enables same-project paste");
            snapshot = await runtime.ExecuteAsync(Command(EditorCommandKind.PasteEntities, []));
            var pastedId = snapshot.Selection.Single();
            Equal(3, snapshot.Entities.Count, "paste adds copied entity");
            Equal(null, snapshot.Entities.Single(entity => entity.EntityId == pastedId).Provenance,
                "paste clears source provenance");
            snapshot = await runtime.ExecuteAsync(Command(EditorCommandKind.Undo, []));
            Equal(2, snapshot.Entities.Count, "paste is undoable");
            Equal(true, snapshot.Selection.SequenceEqual([firstId, secondId]), "undo restores pre-paste selection");
            snapshot = await runtime.ExecuteAsync(Command(EditorCommandKind.DuplicateEntities, [secondId]));
            var duplicateId = snapshot.Selection.Single();
            Equal(true, duplicateId != secondId, "duplicate receives a new entity ID");
            snapshot = await runtime.ExecuteAsync(Command(EditorCommandKind.DeleteEntities, [duplicateId]));
            Equal(2, snapshot.Entities.Count, "delete removes selected entity");
            Equal(true, snapshot.Selection.SequenceEqual([secondId]), "delete chooses a stable selection fallback");
            snapshot = await runtime.ExecuteAsync(Command(EditorCommandKind.Undo, []));
            Equal(true, snapshot.Entities.Any(entity => entity.EntityId == duplicateId), "delete is undoable");
            snapshot = await runtime.ExecuteAsync(Command(EditorCommandKind.Undo, []));
            Equal(2, snapshot.Entities.Count, "duplicate is undoable");
            Equal(true, snapshot.Entities.Single(entity => entity.EntityId == firstId).State.Dirty,
                "mutated entity dirty state");
            snapshot = await runtime.ExecuteAsync(new(
                Guid.NewGuid().ToString("D"), EditorCommandKind.RenameEntity, [firstId], Text: "Renamed entity"));
            snapshot = await runtime.ExecuteAsync(new(
                Guid.NewGuid().ToString("D"), EditorCommandKind.SetEntityLayer, [firstId, secondId], Text: "gameplay"));
            snapshot = await runtime.ExecuteAsync(new(
                Guid.NewGuid().ToString("D"), EditorCommandKind.SetEntityState, [firstId, secondId],
                State: new(Hidden: true, Locked: true)));
            Equal("Renamed entity", snapshot.Entities.Single(entity => entity.EntityId == firstId).Name,
                "entity rename command");
            Equal(true, snapshot.Entities.All(entity => entity.Layer == "gameplay"), "multi-entity layer command");
            Equal(true, snapshot.Entities.All(entity => entity.State is { Hidden: true, Locked: true }),
                "multi-entity state command");
            await ThrowsAsync<ArgumentException>(() => runtime.ExecuteAsync(new(
                Guid.NewGuid().ToString("D"), EditorCommandKind.RenameEntity, [firstId], Text: "Locked rename")));
            await ThrowsAsync<ArgumentException>(() => runtime.ExecuteAsync(
                Command(EditorCommandKind.UpdateTransform, [firstId], ProjectTransform.Identity)));
            await ThrowsAsync<ArgumentException>(() => runtime.ExecuteAsync(
                Command(EditorCommandKind.DeleteEntities, [firstId])));
            snapshot = await runtime.ExecuteAsync(new(
                Guid.NewGuid().ToString("D"), EditorCommandKind.SetEntityState, [firstId],
                State: new(Locked: false)));
            Equal(false, snapshot.Entities.Single(entity => entity.EntityId == firstId).State.Locked,
                "locked entity can be unlocked");
            await WaitForAsync(async () => (await runtime.ReadEventsAsync(0, EditorRuntimeEventLimit))
                .Any(value => value.Kind == EditorEventKind.RecoveryWritten));
            var firstWorkspace = await ForgeProjectWorkspace.OpenAsync(firstPath);
            Equal(true, (await firstWorkspace.ListRecoveriesAsync()).Count > 0, "idle autosave writes recovery");

            snapshot = await runtime.SaveAsync();
            Equal(false, snapshot.IsDirty, "manual save marks runtime clean");
            Equal(true, snapshot.Entities.All(entity => !entity.State.Dirty), "save clears entity dirty state");
            Equal(true, snapshot.CanUndo, "save preserves session history");
            for (var index = 0; index < 101; index++)
                snapshot = await runtime.ExecuteAsync(new(
                    Guid.NewGuid().ToString("D"), EditorCommandKind.RenameProject, [], Text: $"History {index}"));
            var undoCount = 0;
            while (snapshot.CanUndo)
            {
                snapshot = await runtime.ExecuteAsync(Command(EditorCommandKind.Undo, []));
                undoCount++;
            }
            Equal(100, undoCount, "history entry cap");
            Equal("History 0", snapshot.ProjectName, "history evicts the oldest complete command");
            snapshot = await runtime.ExecuteAsync(new(
                Guid.NewGuid().ToString("D"), EditorCommandKind.RenameProject, [], Text: "Renamed"));
            Equal("Renamed", snapshot.ProjectName, "rename command");
            await ThrowsAsync<IOException>(() => runtime.OpenAsync(
                Path.Combine(root, "missing"), TimeSpan.Zero));
            Equal("Renamed", (await runtime.GetSnapshotAsync()).ProjectName, "failed transition preserves active project");
            await runtime.OpenAsync(secondPath, TimeSpan.Zero);
            var reopenedFirst = await ForgeProjectWorkspace.OpenAsync(firstPath);
            Equal(true, (await reopenedFirst.ListRecoveriesAsync()).Any(recovery => recovery.Name == "Renamed"),
                "project transition flushes dirty recovery");

            var events = await runtime.ReadEventsAsync(0, EditorRuntimeEventLimit);
            Equal(true, events.Zip(events.Skip(1)).All(pair => pair.First.Sequence < pair.Second.Sequence),
                "runtime events are ordered");
            _ = JsonSerializer.Serialize(events);
            await runtime.CloseAsync();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task VerifyTieCollisionHistoryAsync(
        string root,
        ProjectTargetProfile target,
        ProjectBaseLevel baseLevel)
    {
        var projectPath = Path.Combine(root, "tie-collision");
        var tieAssetId = AssetId.Parse(new string('a', AssetId.TextLength));
        var firstId = EntityId.New();
        var secondId = EntityId.New();
        await ForgeProjectWorkspace.CreateAsync(projectPath, "TIE collision", target, baseLevel,
        [
            new(firstId, "First TIE", "ties", ProjectTransform.Identity, new(tieAssetId, AssetKind.Tie)),
            new(secondId, "Second TIE", "ties", ProjectTransform.Identity, new(tieAssetId, AssetKind.Tie)),
        ]);
        var catalogPath = Path.Combine(root, "tie-collision-catalog");
        _ = await AssetCatalogStore.OpenAsync(catalogPath);
        await using var runtime = new EditorRuntime();
        await runtime.OpenAsync(projectPath, catalogPath, TimeSpan.Zero);
        var recipe = new ProjectTieCollisionRecipe(
            ProjectTieCollisionRecipeKind.Wrap,
            GeneratorVersion: 5,
            RecipeVersion: 1,
            LodIndex: 0,
            RawType: 0x31,
            DetailSize: 1,
            SealOpeningSize: 2);

        var snapshot = await runtime.ApplyTieCollisionProxyAsync(
            Guid.NewGuid().ToString("D"), tieAssetId, "first proxy"u8.ToArray(), 1, recipe);
        Equal(true, snapshot.Entities.All(entity => entity.State.Dirty),
            "binding marks every matching TIE dirty");
        Equal(true, snapshot.Entities.All(entity => entity.TieCollisionEnabled == true),
            "matching TIEs inherit enabled proxy collision");
        Equal(true, snapshot.Entities.All(entity => entity.TieCollision?.Recipe == recipe),
            "matching TIEs expose the applied binding recipe");
        snapshot = await runtime.ExecuteAsync(Command(EditorCommandKind.Undo, []));
        Equal(false, snapshot.IsDirty, "proxy apply undo restores clean project state");
        snapshot = await runtime.ExecuteAsync(Command(EditorCommandKind.Redo, []));
        Equal(true, snapshot.IsDirty, "proxy apply redo restores binding");
        await runtime.SaveAsync();

        var replacementBytes = "replacement proxy"u8.ToArray();
        snapshot = await runtime.ApplyTieCollisionProxyAsync(
            Guid.NewGuid().ToString("D"), tieAssetId, replacementBytes, 1,
            recipe with { SealOpeningSize = 4 });
        snapshot = await runtime.ExecuteAsync(Command(EditorCommandKind.Undo, []));
        Equal(false, snapshot.IsDirty, "proxy replacement undo restores saved binding");
        snapshot = await runtime.ExecuteAsync(Command(EditorCommandKind.Redo, []));
        Equal(true, snapshot.IsDirty, "proxy replacement redo restores replacement");

        var proxyIdBeforeRawTypeChange = snapshot.Entities[0].TieCollision!.ProxyAssetId;
        snapshot = await runtime.ExecuteAsync(new(
            Guid.NewGuid().ToString("D"),
            EditorCommandKind.SetTieCollisionRawType,
            [firstId],
            TieCollisionRawType: 0xaf));
        Equal((byte)0xaf, snapshot.Entities[0].TieCollision!.Recipe.RawType,
            "collision ID update changes the shared recipe");
        Equal(proxyIdBeforeRawTypeChange, snapshot.Entities[0].TieCollision!.ProxyAssetId,
            "collision ID update reuses the proxy geometry");
        snapshot = await runtime.ExecuteAsync(Command(EditorCommandKind.Undo, []));
        Equal(recipe.RawType, snapshot.Entities[0].TieCollision!.Recipe.RawType,
            "collision ID update is undoable");

        snapshot = await runtime.ExecuteAsync(new(
            Guid.NewGuid().ToString("D"),
            EditorCommandKind.SetTieCollisionEnabled,
            [secondId],
            TieCollisionEnabled: false));
        Equal(false, snapshot.Entities.Single(entity => entity.EntityId == secondId).TieCollisionEnabled,
            "instance collision opt-out command");
        snapshot = await runtime.ExecuteAsync(Command(EditorCommandKind.Undo, []));
        Equal(true, snapshot.Entities.Single(entity => entity.EntityId == secondId).TieCollisionEnabled,
            "instance collision opt-out undo");
        snapshot = await runtime.ExecuteAsync(Command(EditorCommandKind.Redo, []));
        Equal(false, snapshot.Entities.Single(entity => entity.EntityId == secondId).TieCollisionEnabled,
            "instance collision opt-out redo");

        snapshot = await runtime.ExecuteAsync(new(
            Guid.NewGuid().ToString("D"), EditorCommandKind.RemoveTieCollisionProxy, [firstId]));
        Equal(true, snapshot.Entities.All(entity => entity.State.Dirty),
            "proxy removal marks every matching TIE dirty");
        await runtime.SaveAsync();
        var workspace = await ForgeProjectWorkspace.OpenAsync(projectPath);
        Equal(0, workspace.Content.TieCollisionBindings.Count, "proxy removal persists");
        snapshot = await runtime.ExecuteAsync(Command(EditorCommandKind.Undo, []));
        Equal(true, snapshot.IsDirty, "proxy removal undo restores binding after save");
        await runtime.SaveAsync();
        workspace = await ForgeProjectWorkspace.OpenAsync(projectPath);
        Equal(1, workspace.Content.TieCollisionBindings.Count, "restored replacement persists");
        Equal(false,
            workspace.Content.Entities.Single(entity => entity.EntityId == secondId).TieCollisionEnabled,
            "restored binding retains per-instance opt-out");

        var binding = workspace.Content.TieCollisionBindings.Single();
        var proxyId = binding.ProxyAssetId.ToString();
        var proxyPath = Path.Combine(projectPath, "assets", proxyId[..2], $"{proxyId}.blob");
        await File.WriteAllBytesAsync(proxyPath, "corrupt"u8.ToArray());
        snapshot = await runtime.OpenAsync(projectPath, catalogPath, TimeSpan.Zero);
        Equal(true, snapshot.Diagnostics.Any(diagnostic =>
                diagnostic.Code == "tie-collision.proxy-corrupt"
                && diagnostic.Message.Contains("regenerate", StringComparison.OrdinalIgnoreCase)),
            "corrupt proxy reports actionable diagnostic");
        Equal(binding, (await ForgeProjectWorkspace.OpenAsync(projectPath)).Content.TieCollisionBindings.Single(),
            "corrupt proxy preserves binding");

        File.Delete(proxyPath);
        snapshot = await runtime.OpenAsync(projectPath, catalogPath, TimeSpan.Zero);
        Equal(true, snapshot.Diagnostics.Any(diagnostic =>
                diagnostic.Code == "tie-collision.proxy-missing"
                && diagnostic.Message.Contains("project assets folder", StringComparison.OrdinalIgnoreCase)),
            "missing proxy reports actionable diagnostic");
        Equal(binding, (await ForgeProjectWorkspace.OpenAsync(projectPath)).Content.TieCollisionBindings.Single(),
            "missing proxy preserves binding");
        await File.WriteAllBytesAsync(proxyPath, replacementBytes);

        var multiplePath = Path.Combine(root, "multiple-tie-collision-bindings");
        var otherTieAssetId = AssetId.Parse(new string('b', AssetId.TextLength));
        var multiple = await ForgeProjectWorkspace.CreateAsync(
            multiplePath, "Multiple TIE collision bindings", target, baseLevel,
            [
                new(EntityId.New(), "First asset", "ties", ProjectTransform.Identity,
                    new(tieAssetId, AssetKind.Tie)),
                new(EntityId.New(), "Second asset", "ties", ProjectTransform.Identity,
                    new(otherTieAssetId, AssetKind.Tie)),
            ]);
        _ = await multiple.ApplyTieCollisionProxyAsync(
            tieAssetId, "first proxy"u8.ToArray(), 1, recipe);
        _ = await multiple.ApplyTieCollisionProxyAsync(
            otherTieAssetId, "second proxy"u8.ToArray(), 1, recipe);
        await multiple.SaveAsync();
        snapshot = await runtime.OpenAsync(multiplePath, catalogPath, TimeSpan.Zero);
        Equal(2, (await ForgeProjectWorkspace.OpenAsync(multiplePath)).Content.TieCollisionBindings.Count,
            "projects with multiple TIE collision bindings open successfully");
    }

    private static async Task VerifyTieCollisionPreviewAsync(
        string root,
        ProjectTargetProfile target,
        ProjectBaseLevel baseLevel)
    {
        var projectPath = Path.Combine(root, "tie-collision-preview");
        var catalogPath = Path.Combine(root, "tie-collision-preview-catalog");
        var tieAssetId = AssetId.Parse(new string('b', AssetId.TextLength));
        var entityId = EntityId.New();
        await ForgeProjectWorkspace.CreateAsync(projectPath, "TIE collision preview", target, baseLevel,
        [
            new(entityId, "Preview TIE", "ties", ProjectTransform.Identity, new(tieAssetId, AssetKind.Tie)),
        ]);
        await DowngradeProjectToVersionFourAsync(projectPath);
        var fail = false;
        var combinedUnsafe = false;
        var blockNext = false;
        var previewStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EditorTieCollisionGenerationSettings? receivedSettings = null;
        await using var runtime = new EditorRuntime(
            tieCollisionPreviewExecutor: async (_, _, _, settings, token) =>
        {
            receivedSettings = settings;
            if (fail) throw new InvalidDataException("synthetic preview failure");
            if (blockNext)
            {
                previewStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            var recipe = new ProjectTieCollisionRecipe(
                ProjectTieCollisionRecipeKind.Surface, 2, 1, 0, 0);
            EditorTieCollisionCandidate candidate = new(
                EditorTieCollisionPreset.Surface,
                "Decimated mesh",
                recipe,
                "preview collision"u8.ToArray(),
                8,
                6,
                1,
                0,
                0,
                0.5f,
                8,
                [new(0, 0, 0, 6, 8, 6, 96, [])],
                new(2, 20, 4, 0, combinedUnsafe ? 1 : 0, []));
            return [candidate];
        }, tieCollisionSourceInspector: (_, _, assetId, _) => Task.FromResult(
            new EditorTieCollisionSourceInfo(assetId, [0, 2])));
        var snapshot = await runtime.OpenAsync(projectPath, catalogPath, TimeSpan.Zero);
        var sourceInfo = await runtime.InspectTieCollisionSourceAsync(entityId);
        Equal(tieAssetId, sourceInfo.TieAssetId, "source inspection resolves the selected TIE asset");
        Equal(true, sourceInfo.SurfaceLodIndices.SequenceEqual([0, 2]),
            "source inspection returns only available surface LODs");
        Equal(true, snapshot.MigrationPending, "preview migration fixture starts pending");
        var generationSettings = new EditorTieCollisionGenerationSettings(
            0xa7, SurfaceLodIndex: 1);
        var preview = await runtime.PreviewTieCollisionAsync(entityId, generationSettings);
        Equal(generationSettings, receivedSettings, "preview forwards generation settings");
        snapshot = await runtime.GetSnapshotAsync();
        Equal(true, snapshot.IsDirty, "preview preserves pending migration state");
        Equal(null, snapshot.Entities.Single().TieCollision, "preview does not bind collision");
        Equal(null, snapshot.Entities.Single().TieCollisionEnabled,
            "unbound TIE does not report collision as enabled");
        snapshot = await runtime.SaveAsync();
        Equal(false, snapshot.IsDirty, "saving migration does not count as an editor mutation");

        fail = true;
        await ThrowsAsync<InvalidDataException>(() => runtime.PreviewTieCollisionAsync(entityId));
        snapshot = await runtime.ApplyTieCollisionPreviewAsync(
            Guid.NewGuid().ToString("D"), preview.Candidates.Single().Token);
        Equal(true, snapshot.IsDirty, "applying retained preview dirties project");
        Equal(EditorTieCollisionPreset.Surface, preview.Candidates.Single().Preset,
            "preview returns candidate metadata");
        Equal(2, preview.Candidates.Single().CombinedAnalysis?.InstanceCount,
            "preview returns combined project analysis");
        Equal(true, snapshot.Entities.Single().TieCollision is not null,
            "applying preview creates binding snapshot");
        snapshot = await runtime.ExecuteAsync(Command(EditorCommandKind.Undo, []));
        Equal(null, snapshot.Entities.Single().TieCollision, "preview apply is one undoable history entry");
        fail = false;
        combinedUnsafe = true;
        preview = await runtime.PreviewTieCollisionAsync(entityId);
        await ThrowsAsync<InvalidOperationException>(() => runtime.ApplyTieCollisionPreviewAsync(
            Guid.NewGuid().ToString("D"), preview.Candidates.Single().Token));

        var retainedToken = preview.Candidates.Single().Token;
        blockNext = true;
        using (var cancellation = new CancellationTokenSource())
        {
            var cancelled = runtime.PreviewTieCollisionAsync(entityId, cancellationToken: cancellation.Token);
            await previewStarted.Task;
            cancellation.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => cancelled);
        }
        blockNext = false;
        Equal(EditorTieCollisionPreset.Surface,
            (await runtime.GetTieCollisionPreviewCandidateAsync(retainedToken)).Preset,
            "cancelled generation preserves the prior valid preview cache");

        var firstSoakToken = (await runtime.PreviewTieCollisionAsync(entityId)).Candidates.Single().Token;
        string latestSoakToken = firstSoakToken;
        for (var index = 0; index < 32; index++)
            latestSoakToken = (await runtime.PreviewTieCollisionAsync(entityId)).Candidates.Single().Token;
        await ThrowsAsync<InvalidOperationException>(() =>
            runtime.GetTieCollisionPreviewCandidateAsync(firstSoakToken));
        Equal(EditorTieCollisionPreset.Surface,
            (await runtime.GetTieCollisionPreviewCandidateAsync(latestSoakToken)).Preset,
            "repeated generation retains only the latest preview cache");

        preview = await runtime.PreviewTieCollisionAsync(entityId);
        await runtime.ExecuteAsync(new(
            Guid.NewGuid().ToString("D"), EditorCommandKind.RenameEntity, [entityId], Text: "Changed TIE"));
        await ThrowsAsync<InvalidOperationException>(() => runtime.ApplyTieCollisionPreviewAsync(
            Guid.NewGuid().ToString("D"), preview.Candidates.Single().Token));
    }

    private static async Task DowngradeProjectToVersionFourAsync(string projectPath)
    {
        var manifestPath = Path.Combine(projectPath, ForgeProjectWorkspace.ManifestFileName);
        var contentPath = Path.Combine(projectPath, ForgeProjectWorkspace.DefaultContentPath);
        var manifest = JsonNode.Parse(await File.ReadAllBytesAsync(manifestPath))!.AsObject();
        manifest["schemaVersion"] = 4;
        manifest["content"] = ForgeProjectWorkspace.LegacyContentPath;
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString() + "\n");

        await using var compressed = File.OpenRead(contentPath);
        await using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        using var plain = new MemoryStream();
        await gzip.CopyToAsync(plain);
        var content = JsonNode.Parse(plain.ToArray())!.AsObject();
        content["schemaVersion"] = 4;
        content.Remove("tieCollisionBindings");
        var legacyPath = Path.Combine(projectPath, ForgeProjectWorkspace.LegacyContentPath);
        await using var output = File.Create(legacyPath);
        await using var legacyGzip = new GZipStream(output, CompressionLevel.Fastest);
        await legacyGzip.WriteAsync(System.Text.Encoding.UTF8.GetBytes(content.ToJsonString() + "\n"));
    }

    private static async Task VerifyCollisionHistoryAsync(
        string root,
        ProjectTargetProfile target,
        ProjectBaseLevel baseLevel)
    {
        var projectPath = Path.Combine(root, "collision");
        var id = EntityId.New();
        var entity = new ProjectEntity(
            id,
            "Solid #0",
            "collision",
            ProjectTransform.Identity,
            new(AssetId.Parse(new string('c', AssetId.TextLength)), AssetKind.Collision),
            new("UYA", baseLevel.Level, "collision/primary", 0),
            Collision: new(ProjectCollisionPieceKind.Solid, 0, 0, 1, 3, [new(0x21, 1)]));
        await ForgeProjectWorkspace.CreateAsync(projectPath, "Collision", target, baseLevel, [entity]);
        await using var runtime = new EditorRuntime(
            transformCapabilityResolver: UyaEditorCapabilities.ResolveTransformCapabilities);
        var snapshot = await runtime.OpenAsync(projectPath, TimeSpan.Zero);
        Equal(EditorTransformCapabilities.Translate, snapshot.Entities.Single().TransformCapabilities,
            "collision exposes translation only");
        Equal(ProjectCollisionPieceKind.Solid, snapshot.Entities.Single().Collision!.Kind,
            "collision metadata reaches editor snapshot");

        var requested = ProjectTransform.Identity with { Position = new(0.08f, 0.02f, -0.08f) };
        snapshot = await runtime.ExecuteAsync(Command(EditorCommandKind.UpdateTransform, [id], requested));
        Equal(new ProjectVector3(0.0625f, 0, -0.078125f), snapshot.Entities.Single().Transform.Position,
            "solid collision translation is quantized before commit");
        await ThrowsAsync<ArgumentException>(() => runtime.ExecuteAsync(Command(
            EditorCommandKind.UpdateTransform,
            [id],
            requested with { Rotation = new(0, 0, 1, 0) })));
        await ThrowsAsync<ArgumentException>(() => runtime.ExecuteAsync(Command(EditorCommandKind.CopyEntities, [id])));
        await ThrowsAsync<ArgumentException>(() => runtime.ExecuteAsync(new(
            Guid.NewGuid().ToString("D"), EditorCommandKind.SetEntityLayer, [id], Text: "world")));
        snapshot = await runtime.ExecuteAsync(new(
            Guid.NewGuid().ToString("D"), EditorCommandKind.SetEntityState, [id], State: new(Disabled: true)));
        Equal(true, snapshot.Entities.Single().State.Disabled, "collision can be disabled");
        snapshot = await runtime.ExecuteAsync(Command(EditorCommandKind.DeleteEntities, [id]));
        Equal(0, snapshot.Entities.Count, "collision can be deleted");
        snapshot = await runtime.ExecuteAsync(Command(EditorCommandKind.Undo, []));
        Equal(id, snapshot.Entities.Single().EntityId, "collision delete is undoable");
    }

    private static async Task VerifyAssetPlacementHistoryAsync(
        string root,
        ProjectTargetProfile target,
        ProjectBaseLevel baseLevel)
    {
        var projectPath = Path.Combine(root, "placement");
        await ForgeProjectWorkspace.CreateAsync(projectPath, "Placement", target, baseLevel, []);
        var placedId = EntityId.New();
        await using var runtime = new EditorRuntime((_, _, placement, _) => Task.FromResult(new ProjectEntity(
            placedId, "Placed", "mobys", placement.Transform,
            new(placement.AssetId, placement.Kind), Source: new(placement.ClassId, new byte[0x88]))));
        var catalogPath = Path.Combine(root, "placement-catalog");
        await runtime.OpenAsync(projectPath, catalogPath, TimeSpan.Zero);
        var placement = new EditorAssetPlacement(
            AssetId.Parse(new string('a', AssetId.TextLength)), AssetKind.Moby, 0x947,
            ProjectTransform.Identity with { Position = new(1, 2, 3) });
        var snapshot = await runtime.ExecuteAsync(new(
            Guid.NewGuid().ToString("D"), EditorCommandKind.CreateEntityFromAsset, [], Placement: placement));
        Equal(true, snapshot.Selection.SequenceEqual([placedId]), "placement selects created entity");
        Equal(placedId, snapshot.Entities.Single().EntityId, "placement keeps generated entity ID");
        snapshot = await runtime.ExecuteAsync(Command(EditorCommandKind.Undo, []));
        Equal(0, snapshot.Entities.Count, "placement is undoable");
        snapshot = await runtime.ExecuteAsync(Command(EditorCommandKind.Redo, []));
        Equal(placedId, snapshot.Entities.Single().EntityId, "placement redo preserves generated entity ID");
    }

    private static async Task VerifySkyShellHistoryAsync(
        string root,
        ProjectTargetProfile target,
        ProjectBaseLevel baseLevel)
    {
        var assetId = AssetId.Parse(new string('b', AssetId.TextLength));
        var firstId = EntityId.New();
        var secondId = EntityId.New();
        var projectPath = Path.Combine(root, "sky-shells");
        await ForgeProjectWorkspace.CreateAsync(projectPath, "Sky shells", target, baseLevel,
        [
            SkyShell(firstId, assetId, 0),
            SkyShell(secondId, assetId, 1),
        ]);
        await using var runtime = new EditorRuntime(skyShellCommandExecutor: (workspace, _, command, _) =>
        {
            if (command.Kind == EditorCommandKind.UpdateSkyShell)
            {
                var entity = workspace.Content.Entities.Single(value => value.EntityId == command.EntityIds[0]);
                workspace.UpdateSkyShell(entity.EntityId, entity.SkyShell! with
                {
                    InitialRotationRadians = command.SkyShellUpdate!.InitialRotationRadians
                        ?? entity.SkyShell.InitialRotationRadians,
                    AngularVelocityRadiansPerSecond = command.SkyShellUpdate.AngularVelocityRadiansPerSecond
                        ?? entity.SkyShell.AngularVelocityRadiansPerSecond,
                });
            }
            else if (command.Kind == EditorCommandKind.ReorderSkyShell)
            {
                workspace.ReorderSkyShell(command.EntityIds[0], command.DestinationOrder!.Value);
            }
            else if (command.Kind is not (EditorCommandKind.DeleteEntities
                or EditorCommandKind.DuplicateEntities or EditorCommandKind.PasteEntities
                or EditorCommandKind.SetEntityState))
            {
                throw new InvalidOperationException("Unexpected fake sky shell command.");
            }
            return Task.FromResult(command.EntityIds);
        });
        var snapshot = await runtime.OpenAsync(projectPath, Path.Combine(root, "sky-catalog"), TimeSpan.Zero);
        var first = snapshot.Entities.Single(entity => entity.EntityId == firstId);
        Equal(0, first.SkyShell!.Order, "sky shell snapshot order");
        Equal(EditorTransformCapabilities.None, first.TransformCapabilities, "sky shell transform tools disabled");
        Equal(false, first.State.ReadOnly, "sky shell structural commands remain enabled");

        var rotation = new ProjectVector3(0.25f, -0.5f, 0.75f);
        snapshot = await runtime.ExecuteAsync(new(
            Guid.NewGuid().ToString("D"), EditorCommandKind.UpdateSkyShell, [firstId],
            SkyShellUpdate: new(InitialRotationRadians: rotation)));
        Equal(rotation, snapshot.Entities.Single(entity => entity.EntityId == firstId)
            .SkyShell!.InitialRotationRadians, "sky shell rotation update");
        snapshot = await runtime.ExecuteAsync(Command(EditorCommandKind.Undo, []));
        Equal(new ProjectVector3(0, 0, 0), snapshot.Entities.Single(entity => entity.EntityId == firstId)
            .SkyShell!.InitialRotationRadians, "sky shell update undo");

        snapshot = await runtime.ExecuteAsync(new(
            Guid.NewGuid().ToString("D"), EditorCommandKind.ReorderSkyShell, [secondId], DestinationOrder: 0));
        Equal(0, snapshot.Entities.Single(entity => entity.EntityId == secondId).SkyShell!.Order,
            "sky shell reorder command");
        Equal(1, snapshot.Entities.Single(entity => entity.EntityId == firstId).SkyShell!.Order,
            "sky shell reorder normalizes sibling order");
        snapshot = await runtime.ExecuteAsync(Command(EditorCommandKind.DeleteEntities, [secondId]));
        Equal(0, snapshot.Entities.Single(entity => entity.EntityId == firstId).SkyShell!.Order,
            "sky shell deletion normalizes remaining order");
    }

    private static async Task VerifyUyaSkyShellCommandsAsync(
        string root,
        ProjectTargetProfile target,
        ProjectBaseLevel baseLevel)
    {
        var projectPath = Path.Combine(root, "uya-sky-shells");
        var catalogPath = Path.Combine(root, "uya-sky-catalog");
        var catalog = await AssetCatalogStore.OpenAsync(catalogPath);
        var bytes = BuildSkybox();
        var source = new OpaqueContentSource(
            "UYA", "NTSC-U", target.Revision, baseLevel.Level, baseLevel.SourceFingerprint);
        var payload = new UyaBaseLayerPayload(
            BakeLayerId.Sky, "sky.bin", AssetKind.Sky, "level_wad/assets/asset_wad.bin", 0, bytes);
        var entry = (await catalog.PutManyAsync(
            UyaBaseLayerService.CreateCatalogPuts([payload], source, "test-sky"))).Single();
        var firstId = EntityId.New();
        await ForgeProjectWorkspace.CreateAsync(projectPath, "UYA sky commands", target, baseLevel,
            [SkyShell(firstId, entry.Id, 0)]);
        await UyaBaseLayerStore.WriteAsync(projectPath, source, [payload], [entry]);

        await using var runtime = new EditorRuntime(
            skyShellCommandExecutor: UyaSkyShellEditorService.ExecuteAsync);
        var snapshot = await runtime.OpenAsync(projectPath, catalogPath, TimeSpan.Zero);
        snapshot = await runtime.ExecuteAsync(new(
            Guid.NewGuid().ToString("D"),
            EditorCommandKind.AddSkyShellFromAsset,
            [],
            SkyShellSource: new(entry.Id, 0)));
        var addedId = snapshot.Selection.Single();
        Equal(2, snapshot.Entities.Count(entity => entity.SkyShell is not null),
            "UYA sky shell add command");

        var requested = new ProjectVector3(0.1f, -0.2f, 0.3f);
        snapshot = await runtime.ExecuteAsync(new(
            Guid.NewGuid().ToString("D"),
            EditorCommandKind.UpdateSkyShell,
            [addedId],
            SkyShellUpdate: new(InitialRotationRadians: requested)));
        const float tick = MathF.PI / 32768f;
        var expected = new ProjectVector3(
            MathF.Round(requested.X / tick, MidpointRounding.AwayFromZero) * tick,
            MathF.Round(requested.Y / tick, MidpointRounding.AwayFromZero) * tick,
            MathF.Round(requested.Z / tick, MidpointRounding.AwayFromZero) * tick);
        Equal(expected, snapshot.Entities.Single(entity => entity.EntityId == addedId)
            .SkyShell!.InitialRotationRadians, "UYA sky shell native rotation quantization");

        snapshot = await runtime.ExecuteAsync(new(
            Guid.NewGuid().ToString("D"),
            EditorCommandKind.ReorderSkyShell,
            [addedId],
            DestinationOrder: 0));
        Equal(0, snapshot.Entities.Single(entity => entity.EntityId == addedId).SkyShell!.Order,
            "UYA sky shell validated reorder");

        while (snapshot.Entities.Count(entity => entity.SkyShell is not null) < 8)
            snapshot = await runtime.ExecuteAsync(new(
                Guid.NewGuid().ToString("D"),
                EditorCommandKind.AddSkyShellFromAsset,
                [],
                SkyShellSource: new(entry.Id, 0)));
        var sequence = snapshot.LastEventSequence;
        await ThrowsAsync<InvalidDataException>(() => runtime.ExecuteAsync(new(
            Guid.NewGuid().ToString("D"),
            EditorCommandKind.AddSkyShellFromAsset,
            [],
            SkyShellSource: new(entry.Id, 0))));
        snapshot = await runtime.GetSnapshotAsync();
        Equal(8, snapshot.Entities.Count(entity => entity.SkyShell is not null),
            "UYA sky shell limit failure preserves composition");
        Equal(sequence, snapshot.LastEventSequence,
            "UYA sky shell limit failure emits no project event");

        await runtime.SaveAsync();
        await runtime.CloseAsync();
        var reopened = await ForgeProjectWorkspace.OpenAsync(projectPath);
        Equal(true, reopened.Content.Entities.Where(entity => entity.SkyShell is not null)
            .OrderBy(entity => entity.SkyShell!.Order)
            .Select(entity => entity.SkyShell!.Order)
            .SequenceEqual(Enumerable.Range(0, 8)),
            "UYA sky shell order survives save and reopen");
    }

    internal static byte[] BuildSkybox()
    {
        var bytes = new byte[0x50];
        using var writer = new BinaryWriter(new MemoryStream(bytes, writable: true));
        writer.BaseStream.Position = 6;
        writer.Write((short)1);
        writer.BaseStream.Position = 16;
        writer.Write((uint)0x40);
        writer.Write((uint)0x40);
        writer.Write(0x40);
        writer.Write((uint)0);
        writer.Write((uint)0x40);
        writer.BaseStream.Position = 0x40;
        writer.Write((short)0);
        writer.Write((short)1);
        writer.Write((short)2);
        writer.Write((short)-3);
        writer.Write((short)4);
        writer.Write((short)1);
        writer.Write((short)0);
        writer.Write((short)-1);
        return bytes;
    }

    private const int EditorRuntimeEventLimit = 1_024;

    private static ProjectEntity Entity(EntityId id, string name) => new(
        id, name, "mobys", ProjectTransform.Identity, null, new("UYA", 3, "gameplay/core/moby_instances", 0));

    private static ProjectEntity SkyShell(EntityId id, AssetId assetId, int order) => new(
        id,
        $"Sky shell {order + 1}",
        "sky",
        ProjectTransform.Identity,
        new(assetId, AssetKind.Sky),
        new("UYA", 3, "level_wad/assets/sky", order),
        SkyShell: new(order, order, new(0, 0, 0), new(0, 0, 0)));

    private static EditorCommand Command(
        EditorCommandKind kind,
        IReadOnlyList<EntityId> ids,
        ProjectTransform? transform = null) => new(Guid.NewGuid().ToString("D"), kind, ids, transform);

    private static async Task WaitForAsync(Func<Task<bool>> predicate)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (await predicate()) return;
            await Task.Delay(10);
        }
        throw new InvalidOperationException("Timed out waiting for editor runtime state.");
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
