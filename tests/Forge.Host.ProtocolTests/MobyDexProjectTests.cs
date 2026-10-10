using System.IO.Compression;
using System.Text;
using Forge.Host.Domain;
using Forge.Host.Games.UYA;

internal static class MobyDexProjectTests
{
    public static async Task RunAsync()
    {
        var dataset = UyaMobyDexDataset.Create();
        var catalog = new MobyDexCatalog(dataset);
        var builtIn = catalog.Resolve(null, "UYA", 6300)
            ?? throw new InvalidOperationException("Expected built-in UYA MobyDex entry.");
        Equal(MobyDexEntrySource.BuiltIn, builtIn.Source, "built-in source");
        Equal("forge.uya.builtin", builtIn.DatasetId, "built-in dataset ID");
        Equal(2, builtIn.DatasetVersion, "built-in dataset version");
        Equal(75, dataset.Entries.Count, "embedded UYA dataset entry count");
        Equal(75, dataset.Entries.Select(entry => entry.OClass).Distinct().Count(),
            "embedded UYA dataset has unique OClasses");
        Equal(true, dataset.Entries.All(entry => entry.Game == "UYA"),
            "embedded dataset contains only UYA entries");
        Equal(1, dataset.Entries.Count(entry => entry.OClass == 5000),
            "legacy duplicate Ammo Pad entry is deduplicated");
        Equal(null, catalog.Resolve(null, "UYA", 8306), "DL OClass 8306 is excluded from UYA");
        var hill = catalog.Resolve(null, "UYA", 12288)?.Entry
            ?? throw new InvalidOperationException("Expected built-in UYA Hill entry.");
        Equal("array", hill.PVar!.Fields.Single().Definition!.Kind, "UYA Hill cuboids are an array");
        Equal(32, hill.PVar.Fields.Single().Definition!.Count, "UYA Hill cuboid count");
        Equal(MobyDexSchema.CurrentVersion, builtIn.SchemaVersion, "built-in entry schema");
        Equal(null, catalog.Resolve(null, "UYA", 1), "missing entry remains unresolved");

        var replacementEntry = builtIn.Entry with { Name = "Reloaded gravity-bomb ammo" };
        catalog.Reload(dataset with { Version = 3, Entries = [replacementEntry] });
        Equal("Reloaded gravity-bomb ammo", catalog.Resolve(null, "UYA", 6300)!.Entry.Name,
            "reload replaces built-in entry");
        Equal(3, catalog.Dataset.Version, "reload replaces dataset metadata");
        Throws<InvalidDataException>(() => catalog.Reload(
            dataset with { Entries = [replacementEntry, replacementEntry] }));
        Equal("Reloaded gravity-bomb ammo", catalog.Resolve(null, "UYA", 6300)!.Entry.Name,
            "invalid reload retains active dataset");
        catalog.Reload(dataset);

        var duplicate = dataset;
        Throws<InvalidDataException>(() => new MobyDexCatalog(
            duplicate with { Entries = [duplicate.Entries[0], duplicate.Entries[0]] }));
        Throws<InvalidDataException>(() => catalog.Resolve(new(
            ProjectMobyDexSchema.CurrentVersion,
            [duplicate.Entries[0], duplicate.Entries[0]]), "UYA", 6300));

        var root = Path.Combine(Path.GetTempPath(), $"forge-mobydex-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var projectPath = Path.Combine(root, "project");
            var sourceBytes = Enumerable.Range(0, 112).Select(value => (byte)value).ToArray();
            var entity = new ProjectEntity(
                EntityId.New(), "Undocumented moby", "mobys", ProjectTransform.Identity, null,
                new("UYA", 3, "gameplay/core/moby_instances", 0),
                Source: new(1, sourceBytes));
            var workspace = await ForgeProjectWorkspace.CreateAsync(
                projectPath,
                "MobyDex project",
                new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
                new("UYA", "NTSC-U", "1.00", 3, UyaIsoService.SupportedMd5),
                [entity]);
            Equal(true, sourceBytes.SequenceEqual(workspace.Content.Entities[0].Source!.RawRecord),
                "missing MobyDex entry preserves raw moby bytes");

            var custom = builtIn.Entry with
            {
                Name = "Custom gravity-bomb ammo",
                Description = "Project-local whole-entry override.",
            };
            var customJson = MobyDexSchema.Format(custom);
            var invalidJson = Encoding.UTF8.GetBytes(
                Encoding.UTF8.GetString(customJson).Replace("\"length\": 48", "\"length\": 2", StringComparison.Ordinal));

            await using (var runtime = new EditorRuntime())
            {
                var snapshot = await runtime.OpenAsync(projectPath, TimeSpan.Zero);
                var fingerprint = workspace.CurrentFingerprint;
                await ThrowsAsync<MobyDexValidationException>(() => runtime.ExecuteAsync(new(
                    Guid.NewGuid().ToString("D"), EditorCommandKind.ImportMobyDexEntry, [],
                    MobyDexEdit: new(EntryJson: invalidJson))));
                snapshot = await runtime.GetSnapshotAsync();
                Equal(false, snapshot.IsDirty, "invalid import leaves project unchanged");

                snapshot = await runtime.ExecuteAsync(new(
                    Guid.NewGuid().ToString("D"), EditorCommandKind.ImportMobyDexEntry, [],
                    MobyDexEdit: new(EntryJson: customJson)));
                Equal(true, snapshot.IsDirty, "valid import marks project dirty");
                snapshot = await runtime.ExecuteAsync(new(
                    Guid.NewGuid().ToString("D"), EditorCommandKind.Undo, []));
                Equal(false, snapshot.IsDirty, "MobyDex import is undoable");
                snapshot = await runtime.ExecuteAsync(new(
                    Guid.NewGuid().ToString("D"), EditorCommandKind.Redo, []));
                Equal(true, snapshot.IsDirty, "MobyDex import is redoable");
                await runtime.CloseAsync();
                Equal(fingerprint, workspace.CurrentFingerprint, "runtime does not mutate another workspace instance");
            }

            workspace = await ForgeProjectWorkspace.OpenAsync(projectPath);
            Equal(null, workspace.Content.MobyDex, "unsaved import does not replace explicit project state");
            var recovery = (await workspace.ListRecoveriesAsync()).Single();
            await workspace.LoadRecoveryAsync(recovery.Id);
            var resolved = workspace.ResolveMobyDexEntry(catalog, "UYA", 6300)
                ?? throw new InvalidOperationException("Expected project MobyDex override.");
            Equal(MobyDexEntrySource.Project, resolved.Source, "project entry takes precedence");
            Equal(MobyDexCatalog.ProjectDatasetId, resolved.DatasetId, "project dataset ID");
            Equal(ProjectMobyDexSchema.CurrentVersion, resolved.DatasetVersion, "project dataset version");
            Equal(custom.Name, resolved.Entry.Name, "whole custom entry selected");
            Throws<MobyDexValidationException>(() => workspace.ImportMobyDexEntry(invalidJson));
            Equal(custom.Name,
                workspace.ResolveMobyDexEntry(catalog, "UYA", 6300)!.Entry.Name,
                "invalid replacement retains previous whole entry");

            var export = workspace.ExportMobyDexEntry("UYA", 6300);
            Equal(true, export.SequenceEqual(MobyDexSchema.Format(MobyDexSchema.Parse(export))),
                "custom export is canonical");
            Equal(false, Encoding.UTF8.GetString(export).Contains(root, StringComparison.Ordinal),
                "custom export contains no machine path");
            Equal(true, sourceBytes.SequenceEqual(workspace.Content.Entities[0].Source!.RawRecord),
                "custom entry does not replace raw moby bytes");

            await workspace.SaveAsync();
            var archivePath = Path.Combine(root, "mobydex-project.zip");
            ZipFile.CreateFromDirectory(projectPath, archivePath, CompressionLevel.Fastest, false);
            var transferredPath = Path.Combine(root, "transferred");
            ZipFile.ExtractToDirectory(archivePath, transferredPath);
            var transferred = await ForgeProjectWorkspace.OpenAsync(transferredPath);
            Equal(custom.Name,
                transferred.ResolveMobyDexEntry(catalog, "UYA", 6300)!.Entry.Name,
                "ZIP transfer preserves project MobyDex override");

            await using (var runtime = new EditorRuntime())
            {
                var snapshot = await runtime.OpenAsync(transferredPath, TimeSpan.Zero);
                snapshot = await runtime.ExecuteAsync(new(
                    Guid.NewGuid().ToString("D"), EditorCommandKind.RemoveMobyDexEntry, [],
                    MobyDexEdit: new(Game: "UYA", OClass: 6300)));
                Equal(true, snapshot.IsDirty, "MobyDex removal marks project dirty");
                snapshot = await runtime.ExecuteAsync(new(
                    Guid.NewGuid().ToString("D"), EditorCommandKind.Undo, []));
                Equal(false, snapshot.IsDirty, "MobyDex removal is undoable");
                snapshot = await runtime.ExecuteAsync(new(
                    Guid.NewGuid().ToString("D"), EditorCommandKind.Redo, []));
                Equal(true, snapshot.IsDirty, "MobyDex removal is redoable");
                await runtime.SaveAsync();
            }
            transferred = await ForgeProjectWorkspace.OpenAsync(transferredPath);
            Equal(MobyDexEntrySource.BuiltIn,
                transferred.ResolveMobyDexEntry(catalog, "UYA", 6300)!.Source,
                "removing project entry restores built-in resolution");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected {expected}, got {actual}");
    }

    private static void Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static async Task ThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}
