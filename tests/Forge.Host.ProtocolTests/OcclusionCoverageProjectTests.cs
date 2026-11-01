using System.IO.Compression;
using System.Text.Json.Nodes;
using Forge.Host.Domain;
using Forge.Host.Domain.Occlusion;

internal static class OcclusionCoverageProjectTests
{
    internal static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-coverage-project-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var project = await CreateAsync(Path.Combine(root, "project"));
            var attached = await project.AttachAssetAsync(AssetKind.World, 1, "source coverage"u8.ToArray());
            var source = new ProjectAssetReference(attached.Id, attached.Kind);
            project.RestoreState(project.CaptureState() with
            {
                Content = project.Content with { Occlusion = new(1, 5, source) },
            });
            await project.SaveAsync();
            await VerifyMigrationAsync(project);
            project = await ForgeProjectWorkspace.OpenAsync(project.RootPath);
            var recipe = Recipe();
            VerifyVisibilityIdentity(project);
            VerifyIdentityAndValidation(project, recipe);
            project.SetOcclusionCoverage(recipe);
            var recipeHash = project.OcclusionCoverageRecipeFingerprint;
            project.SetPrimaryOcclusionCoverageEnabled(false);
            var authoredHash = project.OcclusionCoverageRecipeFingerprint;
            var recovery = await project.WriteRecoveryAsync() ?? throw new Exception("Coverage edit must create recovery.");
            project.SetOcclusionCoverage(ProjectOcclusionCoverageState.PrimaryOnly);
            await project.LoadRecoveryAsync(recovery.Id);
            Check(project.OcclusionCoverageRecipeFingerprint == authoredHash && !project.Content.OcclusionCoverage.PrimaryEnabled,
                "Recovery must restore the authored recipe and Primary contribution.");
            Check(project.Content.Occlusion?.SourcePayload == source && project.Content.OcclusionCoverage.Sources.Count == 3,
                "Clearing/restoring Primary must retain imported source attachment and authored objects.");
            project.SetPrimaryOcclusionCoverageEnabled(true);
            Check(project.OcclusionCoverageRecipeFingerprint == recipeHash, "Restoring Primary must restore recipe identity.");
            await project.SaveAsync();
            var moved = Path.Combine(root, "portable");
            Directory.Move(project.RootPath, moved);
            project = await ForgeProjectWorkspace.OpenAsync(moved);
            Check(project.OcclusionCoverageRecipeFingerprint == recipeHash && !project.IsDirty,
                "Recipe identity and clean state must survive a project move and reopen.");
            Check((await project.ReadAttachedAssetVerifiedAsync(source, 1024)).AsSpan().SequenceEqual("source coverage"u8),
                "The source coverage attachment must remain portable.");
            await VerifyHistoryAsync(project);
            await VerifyStickyAsync(project, recipe);
            await VerifyAttachedGraphsAsync(project, recipe);
            await VerifyMalformedPersistenceAsync(root);
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task VerifyAttachedGraphsAsync(ForgeProjectWorkspace project, ProjectOcclusionCoverageState recipe)
    {
        var legacy = recipe.Sources[2];
        var box = legacy with { BoxSize = legacy.Graph!.Size, Graph = legacy.Graph with { Size = null } };
        var original = new ProjectOcclusionCoverageState(1, false, [legacy]);
        var attached = original with { Sources = [box] };
        Check(OcclusionCoverageRecipe.Fingerprint(original) == OcclusionCoverageRecipe.Fingerprint(attached),
            "Attaching the legacy graph to the same box preserves semantic coverage identity.");
        var cylinder = box with { BoxSize = null, Cylinder = new(32, 32) };
        attached = attached with { Sources = [cylinder] };
        project.SetOcclusionCoverage(attached); await project.SaveAsync();
        var reopened = await ForgeProjectWorkspace.OpenAsync(project.RootPath);
        Check(reopened.Content.OcclusionCoverage.Sources.Single() is { Cylinder.Radius: 32, Graph.Size: null }
            && reopened.Content.OcclusionCoverage.Sources.Single().Graph!.Markers.Select(m => m.Id).SequenceEqual(legacy.Graph.Markers.Select(m => m.Id)),
            "Cylinder boundaries, seeds and shape ownership survive save/reopen.");
        var off = cylinder with { Graph = cylinder.Graph! with { Disabled = true } };
        Check(OcclusionCoverageRecipe.Fingerprint(attached with { Sources = [off] })
            == OcclusionCoverageRecipe.Fingerprint(attached with { Sources = [cylinder with { Graph = null }] }),
            "A disabled graph retains its seeds without changing plain-shape generation identity.");
        var invalid = cylinder with { Graph = cylinder.Graph! with { Markers = [new(EntityId.New(), "Seed 1", new(31, 31, 0))] } };
        Throws<InvalidDataException>(() => project.SetOcclusionCoverage(attached with { Sources = [invalid] }));
        await using var runtime = new EditorRuntime();
        await runtime.OpenAsync(project.RootPath, TimeSpan.FromDays(1));
        var before = await runtime.QueryOcclusionCoverageAsync();
        await runtime.UpdateOcclusionCoverageAsync(before.ProjectFingerprint, attached with { Sources = [off] });
        await runtime.ExecuteAsync(new(Guid.NewGuid().ToString("D"), EditorCommandKind.Undo, []));
        Check((await runtime.QueryOcclusionCoverageAsync()).State.Sources.Single().Graph is { Disabled: false }, "Undo restores seed filtering and its saved seeds.");
        await runtime.ExecuteAsync(new(Guid.NewGuid().ToString("D"), EditorCommandKind.Redo, []));
        await runtime.SaveAsync();
        Check((await ForgeProjectWorkspace.OpenAsync(project.RootPath)).Content.OcclusionCoverage.Sources.Single().Graph is { Disabled: true },
            "Redo and save preserve an inactive graph rather than deleting it.");
    }

    private static async Task VerifyStickyAsync(ForgeProjectWorkspace project, ProjectOcclusionCoverageState recipe)
    {
        var graph = recipe.Sources[2];
        ProjectOcclusionCoverageState With(bool sticky, float? distance) => recipe with
        { Sources = recipe.Sources.Select(source => source.Id == graph.Id ? graph with { Graph = graph.Graph! with { Sticky = sticky, StickyDistance = distance } } : source).ToArray() };
        var original = OcclusionCoverageRecipe.Fingerprint(recipe);
        Check(OcclusionCoverageRecipe.Fingerprint(With(false, 7)) == original, "Disabled Sticky metadata cannot alter old recipe identity.");
        var sticky = With(true, null);
        var hash = OcclusionCoverageRecipe.Fingerprint(sticky);
        Check(hash != original && hash == OcclusionCoverageRecipe.Fingerprint(With(true, 12))
            && hash != OcclusionCoverageRecipe.Fingerprint(With(true, 4)), "Sticky uses an effective 12-unit default and includes active distance in recipe identity.");
        project.SetOcclusionCoverage(With(true, 4));
        await project.SaveAsync();
        var opened = await ForgeProjectWorkspace.OpenAsync(project.RootPath);
        Check(!opened.IsDirty && opened.Content.OcclusionCoverage.Sources[2].Graph is { Sticky: true, StickyDistance: 4 },
            "Sticky settings must survive save/reopen without becoming dirty.");
        foreach (var invalid in new[] { -1f, float.NaN, float.PositiveInfinity, 1_000_001f })
            Throws<InvalidDataException>(() => project.SetOcclusionCoverage(With(true, invalid)));
    }

    private static void VerifyVisibilityIdentity(ForgeProjectWorkspace project)
    {
        var original = project.CaptureState();
        var fingerprint = project.CurrentFingerprint;
        var visibility = project.OcclusionVisibilityFingerprint;
        var id = project.Content.Entities.Single().EntityId;
        project.SetEntityState([id], true, null, true);
        Check(project.CurrentFingerprint != fingerprint && project.OcclusionVisibilityFingerprint == visibility,
            "Hiding and locking must persist without invalidating visibility, including implicit default entity state.");
        project.SetEntityState([id], null, true, null);
        Check(project.OcclusionVisibilityFingerprint != visibility, "Disabling geometry must invalidate visibility.");
        project.SetEntityState([id], false, false, false);
        Check(project.OcclusionVisibilityFingerprint == visibility, "Explicit default state must match implicit defaults.");
        project.RestoreState(original);
        Check(project.CurrentFingerprint == fingerprint && project.OcclusionVisibilityFingerprint == visibility,
            "Restoring history must restore both identities.");

        var recipe = Recipe();
        project.SetOcclusionCoverage(recipe);
        var recipeHash = project.OcclusionCoverageRecipeFingerprint;
        var authoredFingerprint = project.CurrentFingerprint;
        var disabled = recipe.Sources[0] with { Id = EntityId.New(), Enabled = false };
        project.SetOcclusionCoverage(recipe with
        {
            Sources = [.. recipe.Sources.Select(source => source with
            {
                Name = "Renamed volume",
                Graph = source.Graph is { } graph ? graph with
                {
                    Markers = graph.Markers.Select(marker => marker with { Name = "Old seed copy" }).ToArray(),
                } : null,
            }), disabled with { Transform = disabled.Transform with { Position = new(80, 80, 80) } }],
        });
        Check(project.CurrentFingerprint != authoredFingerprint
            && project.OcclusionCoverageRecipeFingerprint == recipeHash && project.OcclusionVisibilityFingerprint == visibility,
            "Volume/seed names and inactive volume edits persist without invalidating either bake identity.");
        project.SetPrimaryOcclusionCoverageEnabled(false);
        Check(project.OcclusionCoverageRecipeFingerprint != recipeHash && project.OcclusionVisibilityFingerprint == visibility,
            "Effective coverage changes invalidate the recipe independently of scene identity.");
        project.RestoreState(original);
    }

    private static async Task VerifyMigrationAsync(ForgeProjectWorkspace project)
    {
        var manifestPath = Path.Combine(project.RootPath, ForgeProjectWorkspace.ManifestFileName);
        var contentPath = project.ContentFilePath;
        var manifest = JsonNode.Parse(ForgeProjectPersistence.Serialize(project.Manifest))!.AsObject();
        var content = JsonNode.Parse(ForgeProjectPersistence.Serialize(project.Content))!.AsObject();
        manifest["schemaVersion"] = 26; content["schemaVersion"] = 26; content.Remove("occlusionCoverage");
        var manifestBytes = ForgeProjectPersistence.Serialize(manifest);
        var contentBytes = Compress(ForgeProjectPersistence.Serialize(content));
        await File.WriteAllBytesAsync(manifestPath, manifestBytes);
        await File.WriteAllBytesAsync(contentPath, contentBytes);
        var migrated = await ForgeProjectWorkspace.OpenAsync(project.RootPath);
        Check(migrated.Content.SchemaVersion == 27 && migrated.Content.OcclusionCoverage.PrimaryEnabled
            && migrated.Content.OcclusionCoverage.Sources.Count == 0 && !migrated.IsDirty,
            "Schema 26 must open cleanly with Primary-only coverage.");
        Check((await File.ReadAllBytesAsync(manifestPath)).SequenceEqual(manifestBytes)
            && (await File.ReadAllBytesAsync(contentPath)).SequenceEqual(contentBytes),
            "Migration must not rewrite saved files before explicit save.");
        await migrated.SaveAsync();
    }

    private static void VerifyIdentityAndValidation(ForgeProjectWorkspace project, ProjectOcclusionCoverageState recipe)
    {
        var hash = OcclusionCoverageRecipe.Fingerprint(recipe);
        var renamed = recipe with
        {
            Sources = recipe.Sources.Reverse().Select(source => source with
            {
                Id = EntityId.New(), Name = "Renamed source",
                Graph = source.Graph is { } graph ? graph with
                {
                    Markers = graph.Markers.Reverse().Select(marker => marker with { Id = EntityId.New(), Name = "Renamed marker" }).ToArray(),
                } : null,
            }).ToArray(),
        };
        Check(hash == OcclusionCoverageRecipe.Fingerprint(renamed), "Names, IDs, source order and marker order must not change semantic coverage.");
        var disabled = recipe.Sources[0] with { Id = EntityId.New(), Enabled = false };
        Check(hash == OcclusionCoverageRecipe.Fingerprint(recipe with { Sources = [.. recipe.Sources, disabled] }),
            "Disabled sources must not affect effective recipe identity.");
        Check(hash == OcclusionCoverageRecipe.Fingerprint(recipe with { Sources = [.. recipe.Sources, recipe.Sources[0] with { Id = EntityId.New() }] }),
            "Duplicate semantic sources must not affect union identity.");
        Check(hash != OcclusionCoverageRecipe.Fingerprint(recipe with { PrimaryEnabled = false }), "Primary contribution must affect coverage identity.");

        var box = recipe.Sources[0];
        var graph = recipe.Sources[2].Graph!;
        var invalidSources = new[]
        {
            box with { Id = default }, box with { Name = " " }, box with { Role = (ProjectOcclusionCoverageRole)99 },
            box with { BoxSize = null }, box with { Cylinder = new(1, 2) },
            box with { BoxSize = new(0, 1, 1) }, box with { BoxSize = new(float.NaN, 1, 1) },
            box with { Transform = box.Transform with { Scale = new(-1, 1, 1) } },
            box with { Transform = box.Transform with { Rotation = new(0, 0, 0, 2) } },
            box with { Transform = box.Transform with { Position = new(float.PositiveInfinity, 0, 0) } },
            recipe.Sources[1] with { Cylinder = new(-1, 2) },
            recipe.Sources[2] with { Role = ProjectOcclusionCoverageRole.Exclude },
            recipe.Sources[2] with { Graph = graph with { Markers = [graph.Markers[0] with { Position = new(100, 0, 0) }] } },
            recipe.Sources[2] with { Graph = graph with { Markers = [graph.Markers[0], graph.Markers[0]] } },
            recipe.Sources[2] with { Graph = graph with { Markers = null! } },
        };
        var before = project.CurrentFingerprint;
        foreach (var source in invalidSources)
        {
            Throws<InvalidDataException>(() => project.SetOcclusionCoverage(recipe with { Sources = [source] }));
            Check(project.CurrentFingerprint == before, "Invalid coverage must not partially mutate the project.");
        }
        foreach (var invalid in new[]
        {
            recipe with { SchemaVersion = 2 }, recipe with { Sources = null! },
            recipe with { Sources = [box, box] }, recipe with { Sources = [null!] },
            recipe with { Sources = Enumerable.Repeat(box, ProjectOcclusionCoverageSchema.MaximumSources + 1).ToArray() },
            recipe with { Sources = [recipe.Sources[2] with { Graph = graph with
                { Markers = Enumerable.Repeat(graph.Markers[0], ProjectOcclusionCoverageSchema.MaximumMarkersPerGraph + 1).ToArray() } }] },
        }) Throws<InvalidDataException>(() => project.SetOcclusionCoverage(invalid));

        var markers = graph.Markers.ToList();
        var sources = new List<ProjectOcclusionCoverageSource> { recipe.Sources[2] with { Graph = graph with { Markers = markers } } };
        project.SetOcclusionCoverage(recipe with { Sources = sources });
        var copiedHash = project.OcclusionCoverageRecipeFingerprint;
        markers.Clear(); sources.Clear();
        Check(project.Content.OcclusionCoverage.Sources.Count == 1
            && project.Content.OcclusionCoverage.Sources[0].Graph!.Markers.Count == 2
            && project.OcclusionCoverageRecipeFingerprint == copiedHash, "Commit must copy caller-owned source and marker lists.");
    }

    private static async Task VerifyHistoryAsync(ForgeProjectWorkspace project)
    {
        await using var runtime = new EditorRuntime();
        var snapshot = await runtime.OpenAsync(project.RootPath, TimeSpan.FromDays(1));
        var entity = snapshot.Entities.Single().EntityId;
        await runtime.ExecuteAsync(new(Guid.NewGuid().ToString("D"), EditorCommandKind.SetSelection, [entity]));
        var initial = await ReadAsync(runtime);
        var edited = initial.Coverage with { PrimaryEnabled = false };
        snapshot = (await runtime.UpdateOcclusionCoverageAsync(initial.Fingerprint, edited)).Project;
        Check(snapshot.IsDirty && snapshot.CanUndo && snapshot.Selection.SequenceEqual([entity]),
            "Coverage edits must join history without changing game-entity selection.");
        var current = await ReadAsync(runtime);
        await ThrowsAsync<InvalidOperationException>(() => runtime.UpdateOcclusionCoverageAsync(initial.Fingerprint, initial.Coverage));
        await ThrowsAsync<InvalidDataException>(() => runtime.UpdateOcclusionCoverageAsync(current.Fingerprint, edited with { SchemaVersion = 2 }));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => runtime.UpdateOcclusionCoverageAsync(current.Fingerprint, initial.Coverage, cancellation.Token));
        snapshot = await runtime.ExecuteAsync(new(Guid.NewGuid().ToString("D"), EditorCommandKind.Undo, []));
        Check(!snapshot.IsDirty && snapshot.CanRedo && (await ReadAsync(runtime)).Fingerprint == initial.Fingerprint,
            "One undo must restore the recipe and saved project identity.");
        await runtime.ExecuteAsync(new(Guid.NewGuid().ToString("D"), EditorCommandKind.Redo, []));
        snapshot = await runtime.SaveAsync();
        Check(!snapshot.IsDirty, "Coverage save must mark the editor clean.");
        var reopened = await ForgeProjectWorkspace.OpenAsync(project.RootPath);
        Check(!reopened.Content.OcclusionCoverage.PrimaryEnabled && reopened.Content.Entities.Count == 1,
            "History edits must persist without adding gameplay entities.");
        var unchanged = await ReadAsync(runtime);
        var eventSequence = snapshot.LastEventSequence;
        snapshot = (await runtime.UpdateOcclusionCoverageAsync(unchanged.Fingerprint, unchanged.Coverage)).Project;
        Check(snapshot.LastEventSequence == eventSequence, "A no-op coverage edit must not emit a project-change event.");
    }

    private static async Task VerifyMalformedPersistenceAsync(string root)
    {
        var project = await CreateAsync(Path.Combine(root, "malformed"));
        var content = JsonNode.Parse(ForgeProjectPersistence.Serialize(project.Content))!.AsObject();
        content["occlusionCoverage"] = null;
        await File.WriteAllBytesAsync(project.ContentFilePath, Compress(ForgeProjectPersistence.Serialize(content)));
        await ThrowsAsync<InvalidDataException>(() => ForgeProjectWorkspace.OpenAsync(project.RootPath));
    }

    private static Task<(string Fingerprint, ProjectOcclusionCoverageState Coverage)> ReadAsync(EditorRuntime runtime) =>
        runtime.ReadWorkspaceAsync((workspace, _) => Task.FromResult((workspace.CurrentFingerprint, workspace.Content.OcclusionCoverage)));

    private static Task<ForgeProjectWorkspace> CreateAsync(string path) => ForgeProjectWorkspace.CreateAsync(path, "Coverage",
        new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"), new("UYA", "NTSC-U", "1.00", 3, new string('a', 32)),
        [new(EntityId.New(), "Unrelated object", "misc", ProjectTransform.Identity, null)]);

    private static ProjectOcclusionCoverageState Recipe() => new(1, true,
    [
        new(EntityId.New(), "Courtyard", true, ProjectOcclusionCoverageRole.Include, ProjectTransform.Identity, BoxSize: new(16, 24, 8)),
        new(EntityId.New(), "Tower", true, ProjectOcclusionCoverageRole.Exclude, ProjectTransform.Identity, Cylinder: new(4, 16)),
        new(EntityId.New(), "Interior", true, ProjectOcclusionCoverageRole.Include, ProjectTransform.Identity,
            Graph: new(new(32, 32, 32), [new(EntityId.New(), "Entrance", new(1, 2, 3)), new(EntityId.New(), "Landing", new(-1, 2, 4))])),
    ]);

    private static byte[] Compress(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true)) gzip.Write(bytes);
        return output.ToArray();
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
}
