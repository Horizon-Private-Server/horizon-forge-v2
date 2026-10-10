using Forge.Host.Domain;
using Forge.Host.Games.UYA;

internal static class GroupTemplateTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-group-templates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var first = EntityId.New();
            var second = EntityId.New();
            var manual = EntityId.New();
            var added = EntityId.New();
            var ambiguous = EntityId.New();
            var assetId = AssetId.Parse(new string('a', AssetId.TextLength));
            var entities = new[]
            {
                Entity(first, "First", 0, assetId),
                Entity(second, "Second", 10),
                Entity(manual, "Manual", 100),
                Entity(added, "Added", 15),
                Entity(ambiguous, "Ambiguous", 18),
            };
            var projectPath = Path.Combine(root, "project");
            await ForgeProjectWorkspace.CreateAsync(
                projectPath,
                "Templates",
                new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
                new("UYA", "NTSC-U", "1.00", 40, UyaIsoService.SupportedMd5),
                entities);

            var generation = 1;
            var evaluatorCalls = 0;
            async Task<EditorGroupTemplateEvaluation> EvaluateAsync(
                ForgeProjectWorkspace workspace,
                EditorGroupTemplateQuery query,
                EditorGroupTemplateRequest request,
                CancellationToken cancellationToken)
            {
                evaluatorCalls++;
                Equal(["distance", "team"], request.Parameters.Select(value => value.Key),
                    "runtime normalizes template parameters");
                Equal(2, query.Nearby(new(0, 0, 0), 12).Count,
                    "spatial query returns only nearby matching entities");
                Equal(assetId, query.Outgoing(first, ProjectReferences.EntityAsset).Single().Reference.AssetId,
                    "template query exposes typed references");
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                var included = generation == 1
                    ? new[] { second, first }
                    : new[] { manual, added, second, first };
                return new(
                    "Generated cluster",
                    included.Select(entityId => new EditorGroupTemplateCandidate(
                            entityId,
                            EditorGroupTemplateCandidateDisposition.Included,
                            ["within distance", "same team", "within distance"]))
                        .Append(new(
                            ambiguous,
                            EditorGroupTemplateCandidateDisposition.Ambiguous,
                            ["equidistant candidate"]))
                        .Reverse()
                        .ToArray(),
                    [new("group-template.ambiguous", EditorDiagnosticSeverity.Warning, "One candidate is ambiguous.")]);
            }

            await using var runtime = new EditorRuntime(
                groupTemplateEvaluator: EvaluateAsync,
                groupTemplates: [TestDefinition()]);
            _ = await runtime.OpenAsync(projectPath, TimeSpan.Zero);
            Equal(true, runtime.HasCapability("editor.group-template.preview"),
                "group template preview capability advertised");
            var request = new EditorGroupTemplateRequest(
                "uya.test-cluster",
                1,
                [new("team", 0), new("distance", 12)],
                SeedEntityId: first);
            var preview = await runtime.PreviewGroupTemplateAsync(request);
            var initialGenerated = Sorted(first, second);
            Equal(initialGenerated, preview.Members, "generated members are stable-ID sorted");
            Equal(3, preview.CandidateCount, "preview reports the complete candidate count");
            Equal(1, preview.AmbiguousCandidateCount, "preview reports the complete ambiguity count");
            Equal(Sorted(first, second, ambiguous), preview.Candidates.Select(value => value.EntityId),
                "candidate evidence is deterministically sorted");
            Equal(["same team", "within distance"],
                preview.Candidates.Single(value => value.EntityId == first).Evidence,
                "candidate evidence is deduplicated and sorted");
            Equal([ambiguous], preview.Candidates
                .Where(value => value.Disposition == EditorGroupTemplateCandidateDisposition.Ambiguous)
                .Select(value => value.EntityId), "ambiguous candidates remain outside membership");

            var repeated = await runtime.PreviewGroupTemplateAsync(request);
            Equal(preview.Members, repeated.Members, "repeat evaluation keeps deterministic membership");
            Equal(
                preview.Candidates.Select(value => $"{value.EntityId}:{value.Disposition}:{string.Join('|', value.Evidence)}"),
                repeated.Candidates.Select(value => $"{value.EntityId}:{value.Disposition}:{string.Join('|', value.Evidence)}"),
                "repeat evaluation keeps deterministic evidence");
            var snapshot = await runtime.ApplyGroupTemplatePreviewAsync(CommandId(), repeated.Token);
            var group = snapshot.Groups.Single();
            Equal("uya.test-cluster", group.Template!.TemplateId, "accepted group stores template identity");
            Equal(["distance", "team"], group.Template.Parameters.Select(value => value.Key),
                "accepted group stores normalized parameters");
            Equal(initialGenerated, group.Template.GeneratedMembers,
                "accepted group stores generated membership provenance");

            snapshot = await runtime.ExecuteAsync(new(
                CommandId(), EditorCommandKind.AddGroupMembers, [manual], GroupEdit: new(group.GroupId)));
            snapshot = await runtime.ExecuteAsync(new(
                CommandId(), EditorCommandKind.RemoveGroupMembers, [second], GroupEdit: new(group.GroupId)));
            group = snapshot.Groups.Single();
            Equal(initialGenerated.Where(value => value != second).Append(manual), group.Members,
                "manual edits change ordinary membership");
            Equal(initialGenerated, group.Template!.GeneratedMembers,
                "manual edits preserve the generated baseline");

            generation = 2;
            var regeneration = await runtime.PreviewGroupTemplateAsync(request with { GroupId = group.GroupId });
            var regenerated = Sorted(first, added).Append(manual).ToArray();
            Equal(regenerated, regeneration.Members,
                "regeneration preserves manual additions and removals");
            Equal([manual], regeneration.ManualAdditions, "manual additions are disclosed");
            Equal([second], regeneration.ManualRemovals, "manual removals are disclosed");
            Equal([added], regeneration.AddedMembers, "regeneration previews additions");
            Equal([], regeneration.RemovedMembers, "regeneration previews removals");

            snapshot = await runtime.ApplyGroupTemplatePreviewAsync(CommandId(), regeneration.Token);
            group = snapshot.Groups.Single();
            Equal(regenerated, group.Members, "confirmed regeneration applies the preview exactly");
            Equal(Sorted(first, second, added), group.Template!.GeneratedMembers,
                "regeneration updates generated provenance without absorbing manual membership");
            snapshot = await runtime.ExecuteAsync(new(CommandId(), EditorCommandKind.Undo, []));
            Equal(initialGenerated.Where(value => value != second).Append(manual), snapshot.Groups.Single().Members,
                "regeneration is one undoable mutation");
            snapshot = await runtime.ExecuteAsync(new(CommandId(), EditorCommandKind.Redo, []));
            Equal(regenerated, snapshot.Groups.Single().Members, "regeneration redo is atomic");

            var stale = await runtime.PreviewGroupTemplateAsync(request with { GroupId = group.GroupId });
            _ = await runtime.ExecuteAsync(new(
                CommandId(), EditorCommandKind.RenameGroup, [], Text: "Changed after preview",
                GroupEdit: new(group.GroupId)));
            await ThrowsAsync<InvalidOperationException>(() =>
                runtime.ApplyGroupTemplatePreviewAsync(CommandId(), stale.Token));

            await runtime.SaveAsync();
            var reopened = await ForgeProjectWorkspace.OpenAsync(projectPath);
            Equal("uya.test-cluster", reopened.Content.Groups.Single().Template!.TemplateId,
                "template provenance survives save and reopen");
            Equal(true, evaluatorCalls >= 4, "preview invokes the adapter-owned evaluator");

            await VerifyCancellationAsync(projectPath, request);
            await VerifyPreviewBoundAsync(root);
            VerifySpatialScale(reopened.Content, first);
            _ = reopened.RemoveEntities([first]);
            Equal(null, reopened.Content.Groups.Single().Template,
                "deleting a generated group's seed preserves it as an ordinary custom group");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static async Task VerifyCancellationAsync(string projectPath, EditorGroupTemplateRequest request)
    {
        static async Task<EditorGroupTemplateEvaluation> Cancelled(
            ForgeProjectWorkspace workspace,
            EditorGroupTemplateQuery query,
            EditorGroupTemplateRequest request,
            CancellationToken cancellationToken)
        {
            _ = workspace;
            _ = query;
            _ = request;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        }

        await using var runtime = new EditorRuntime(
            groupTemplateEvaluator: Cancelled,
            groupTemplates: [TestDefinition()]);
        _ = await runtime.OpenAsync(projectPath, TimeSpan.Zero);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        await ThrowsAsync<OperationCanceledException>(() =>
            runtime.PreviewGroupTemplateAsync(request, cancellation.Token));
    }

    private static async Task VerifyPreviewBoundAsync(string root)
    {
        var assetId = AssetId.Parse(new string('b', AssetId.TextLength));
        var entities = Enumerable.Range(0, 300)
            .Select(index => Entity(EntityId.New(), $"Candidate {index}", index, assetId))
            .ToArray();
        var projectPath = Path.Combine(root, "bounded-preview");
        await ForgeProjectWorkspace.CreateAsync(
            projectPath,
            "Bounded preview",
            new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
            new("UYA", "NTSC-U", "1.00", 40, UyaIsoService.SupportedMd5),
            entities);
        Task<EditorGroupTemplateEvaluation> EvaluateAsync(
            ForgeProjectWorkspace workspace,
            EditorGroupTemplateQuery query,
            EditorGroupTemplateRequest request,
            CancellationToken cancellationToken)
        {
            _ = workspace;
            _ = query;
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new EditorGroupTemplateEvaluation(
                "Bounded candidates",
                entities.Select((entity, index) => new EditorGroupTemplateCandidate(
                    entity.EntityId,
                    index < 125
                        ? EditorGroupTemplateCandidateDisposition.Ambiguous
                        : EditorGroupTemplateCandidateDisposition.Included,
                    [$"candidate {index}"])).ToArray(),
                []));
        }

        await using var runtime = new EditorRuntime(
            groupTemplateEvaluator: EvaluateAsync,
            groupTemplates: [TestDefinition()]);
        _ = await runtime.OpenAsync(projectPath, TimeSpan.Zero);
        var preview = await runtime.PreviewGroupTemplateAsync(new(
            "uya.test-cluster",
            1,
            [new("distance", 12), new("team", 0)],
            SeedEntityId: entities[0].EntityId));
        Equal(300, preview.CandidateCount, "large preview reports all candidates");
        Equal(125, preview.AmbiguousCandidateCount, "large preview reports all ambiguities");
        Equal(250, preview.Candidates.Count, "large preview bounds transported candidate details");
        Equal(true, preview.Candidates.Take(125).All(candidate =>
                candidate.Disposition == EditorGroupTemplateCandidateDisposition.Ambiguous),
            "bounded preview prioritizes ambiguous candidate details");
        Equal(175, preview.Members.Count, "preview membership is not truncated with candidate details");
    }

    private static void VerifySpatialScale(ForgeProjectContent source, EntityId expected)
    {
        var extreme = EntityId.New();
        var distant = Enumerable.Range(1, 25_000).Select(index =>
            Entity(EntityId.New(), $"Distant {index}", index * 100f));
        var query = new EditorGroupTemplateQuery(source with
        {
            Entities = [source.Entities.Single(value => value.EntityId == expected),
                Entity(extreme, "Extreme", float.MaxValue), .. distant],
        });
        Equal([expected], query.Nearby(new(0, 0, 0), 16).Select(value => value.EntityId),
            "spatial index bounds a representative large-project proximity query");
        Equal([extreme], query.Nearby(new(float.MaxValue, 0, 0), 16).Select(value => value.EntityId),
            "spatial index does not duplicate saturated coordinate cells");
    }

    private static ProjectEntity Entity(EntityId id, string name, float x, AssetId? assetId = null) => new(
        id,
        name,
        "entities",
        ProjectTransform.Identity with { Position = new(x, 0, 0) },
        assetId is { } value ? new(value, AssetKind.Moby) : null);

    private static string CommandId() => Guid.NewGuid().ToString("D");

    private static EntityId[] Sorted(params EntityId[] values) => values
        .OrderBy(value => value.ToString(), StringComparer.Ordinal).ToArray();

    private static EditorGroupTemplateDefinition TestDefinition() => new(
        "uya.test-cluster",
        1,
        "Test cluster",
        "Synthetic group template qualification.",
        ProjectEntityKind.Moby,
        [new("distance", "Distance", 12, 0, 100, 1), new("team", "Team", 0, 0, 3, 1)]);

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
