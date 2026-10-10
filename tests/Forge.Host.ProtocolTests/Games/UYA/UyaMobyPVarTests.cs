using System.Buffers.Binary;
using Forge.Host.Domain;
using Forge.Host.Games.UYA;
using RatchetPs2.Core.Gameplay;
using RatchetPs2.Games.UYA.Gameplay;

namespace Forge.Host.ProtocolTests.Games.UYA;

internal static class UyaMobyPVarTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-moby-pvars-{Guid.NewGuid():N}");
        try
        {
            var first = Moby(0x400, 0, 0);
            var second = Moby(0x400, 1, 0);
            var withDefault = Moby(0x400, 2, -1);
            var withoutDefault = Moby(0x401, 3, -1);
            var data = new byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(data, 1);
            data[4] = 0x55;
            var tables = GameplayPvarTableWriter.Write([
                new(data, [0], [4]),
            ]);
            var catalog = Catalog();
            var entities = new[] { first, second, withDefault, withoutDefault };
            var imported = UyaMobyPVarService.Import(
                entities.Concat([Collision(0), Collision(1)]).ToArray(), tables, catalog)
                ?? throw new InvalidOperationException("PVar import returned no state.");
            Equal(2, imported.Entries.Count, "shared native PVar imports per moby");
            Equal(true, imported.Entries.All(value => value.Data.SequenceEqual(value.BaselineData)),
                "imported PVars retain their source-byte baseline");
            var migrated = ProjectMobyPVars.Migrate(new(
                ProjectSchema.CurrentVersion,
                ProjectSchema.ContentDocumentType,
                entities,
                [],
                [],
                [],
                MobyPVars: new(1, imported.SourceTableEntryCount, imported.Entries.Select(value =>
                    value with { BaselineData = null! }).ToArray())));
            Equal(ProjectMobyPVarSchema.CurrentVersion, migrated.MobyPVars!.SchemaVersion,
                "legacy PVar state migrates to the baseline-aware schema");
            Equal(true, migrated.MobyPVars.Entries.All(value => value.Data.SequenceEqual(value.BaselineData)),
                "legacy PVar migration uses its current bytes as a safe baseline");
            Equal(0, imported.Entries[0].SourceTableIndex, "first PVar retains native table slot");
            Equal(null, imported.Entries[1].SourceTableIndex, "shared PVar receives an independent slot");
            Equal(second.EntityId, imported.Entries[0].References.Single().Reference.EntityId,
                "native moby link resolves to a stable entity ID");
            var projectTargetSource = Moby(0x400, 1, -1);
            var projectTarget = projectTargetSource with
            {
                Provenance = null,
                Source = projectTargetSource.Source! with { SourceIndex = 1 },
            };
            var projectResolved = UyaMobyPVarService.Import([first, projectTarget], tables, catalog)!;
            Equal(projectTarget.EntityId, projectResolved.Entries.Single().References.Single().Reference.EntityId,
                "source-index lookup resolves project-created targets without rescanning entities");
            var duplicateTarget = Moby(0x400, 1, -1);
            var ambiguous = UyaMobyPVarService.Import([first, second, duplicateTarget], tables, catalog)!;
            Equal<EntityId?>(null, ambiguous.Entries[0].References.Single().Reference.EntityId,
                "source-index lookup preserves ambiguous references as unresolved");
            await VerifyMissingReferenceAsync(root, catalog);

            var project = Path.Combine(root, "project");
            var workspace = await ForgeProjectWorkspace.CreateAsync(
                project,
                "Moby PVars",
                new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
                new("UYA", "NTSC-U", "1.00", 3, new string('a', 32)),
                entities,
                null,
                null,
                null,
                imported);
            var replacement = imported.Entries[0].Data.ToArray();
            replacement[7] = 0xaa;
            workspace.ReplaceMobyPVarData(first.EntityId, replacement);
            Equal((byte)0, workspace.Content.MobyPVars!.Entries[0].BaselineData[7],
                "PVar edits do not mutate the source-byte baseline");
            Equal((byte)0, workspace.Content.MobyPVars!.Entries[1].Data[7],
                "editing one shared source PVar does not mutate another");
            await ThrowsAsync<InvalidDataException>(() => Task.FromResult(
                workspace.ReplaceMobyPVarData(first.EntityId, new byte[7])));

            var copy = workspace.AddCopies([first]).Single();
            var copyPVar = workspace.Content.MobyPVars!.Entries.Single(value => value.EntityId == copy.EntityId);
            Equal(null, copyPVar.SourceTableIndex, "copied PVar receives a new native slot");
            Equal(true, copyPVar.Data.SequenceEqual(replacement), "copied PVar preserves opaque bytes");

            var initialized = UyaMobyPVarService.CreateDefault(workspace, catalog, withDefault.EntityId);
            Equal(8, initialized.Data.Length, "verified default initializes exact PVar length");
            Equal((byte)0x7f, initialized.Data[4], "verified default bytes are used");
            await ThrowsAsync<InvalidOperationException>(() => Task.FromResult(
                UyaMobyPVarService.CreateDefault(workspace, catalog, withoutDefault.EntityId)));

            var reordered = workspace.Content with { Entities = workspace.Content.Entities.Reverse().ToArray() };
            var reorderedTables = UyaMobyPVarService.Rebuild(reordered, tables, new List<string>());
            Equal(1, BinaryPrimitives.ReadInt32LittleEndian(reorderedTables.Entries[0].Data),
                "stable PVar references survive project entity-list reordering");

            workspace.RemoveEntity(second.EntityId);
            Equal(null, workspace.Content.MobyPVars!.Entries.Single(value => value.EntityId == first.EntityId)
                .References.Single().Reference.EntityId, "deleting a target clears nullable PVar links");
            var rebuilt = UyaMobyPVarService.Rebuild(workspace.Content, tables, new List<string>());
            Equal(-1, BinaryPrimitives.ReadInt32LittleEndian(rebuilt.Entries[0].Data),
                "native rebuild encodes a cleared moby link sentinel");
            Equal((byte)0xaa, rebuilt.Entries[0].Data[7],
                "native rebuild preserves edited opaque bytes outside reference fields");
            Equal(3, rebuilt.Entries.Count, "native rebuild preserves source slots and appends independent PVars");

            await workspace.SaveAsync();
            var reopened = await ForgeProjectWorkspace.OpenAsync(project);
            Equal(true, reopened.Content.MobyPVars!.Entries.Single(value => value.EntityId == first.EntityId)
                .Data.SequenceEqual(replacement), "PVar bytes survive project save and reload");
            Equal((byte)0, reopened.Content.MobyPVars!.Entries.Single(value => value.EntityId == first.EntityId)
                .BaselineData[7], "PVar baseline survives project save and reload");

            var historyProject = Path.Combine(root, "history-project");
            var historyMoby = Moby(0x400, 0, -1);
            await ForgeProjectWorkspace.CreateAsync(
                historyProject,
                "PVar history",
                new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
                new("UYA", "NTSC-U", "1.00", 3, new string('b', 32)),
                [historyMoby]);
            await using var runtime = new EditorRuntime(mobyPVarCommandExecutor: (active, command, token) =>
                UyaMobyPVarService.ExecuteInitializeAsync(active, command, catalog, token));
            var snapshot = await runtime.OpenAsync(historyProject, TimeSpan.Zero);
            snapshot = await runtime.ExecuteAsync(new(
                Guid.NewGuid().ToString("D"), EditorCommandKind.InitializeMobyPVar, [historyMoby.EntityId]));
            Equal(true, snapshot.IsDirty && snapshot.CanUndo, "PVar initialization is undoable");
            snapshot = await runtime.ExecuteAsync(new(
                Guid.NewGuid().ToString("D"), EditorCommandKind.Undo, []));
            Equal(false, snapshot.IsDirty, "undo restores the missing PVar state");
            snapshot = await runtime.ExecuteAsync(new(
                Guid.NewGuid().ToString("D"), EditorCommandKind.Redo, []));
            Equal(true, snapshot.IsDirty, "redo restores the initialized PVar state");
            await runtime.SaveAsync();
            reopened = await ForgeProjectWorkspace.OpenAsync(historyProject);
            Equal((byte)0x7f, reopened.Content.MobyPVars!.Entries.Single().Data[4],
                "initialized PVar persists through the editor save path");

            var rejectedProject = Path.Combine(root, "rejected-project");
            var rejectedMoby = Moby(0x401, 0, -1);
            await ForgeProjectWorkspace.CreateAsync(
                rejectedProject,
                "PVar without default",
                new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
                new("UYA", "NTSC-U", "1.00", 3, new string('c', 32)),
                [rejectedMoby]);
            await runtime.OpenAsync(rejectedProject, TimeSpan.Zero);
            await ThrowsAsync<InvalidOperationException>(() => runtime.ExecuteAsync(new(
                Guid.NewGuid().ToString("D"), EditorCommandKind.InitializeMobyPVar, [rejectedMoby.EntityId])));
            Equal(false, (await runtime.GetSnapshotAsync()).IsDirty,
                "missing defaults cannot partially initialize a PVar");

            await VerifySchemaIndependentImportAsync(root);
            await VerifyStructuredEditorAsync(root);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task VerifySchemaIndependentImportAsync(string root)
    {
        var project = Path.Combine(root, "schema-independent-import");
        var moby = Moby(0x499, 0, 0);
        var fingerprint = new string('e', 32);
        var data = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(data, 7);
        var tables = GameplayPvarTableWriter.Write([new(data, [], [])]);
        await ForgeProjectWorkspace.CreateAsync(
            project,
            "Schema-independent PVars",
            new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
            new("UYA", "NTSC-U", "1.00", 3, fingerprint),
            [moby]);
        await UyaGameplayLayerStore.WriteSourceAsync(
            project,
            new("UYA", "NTSC-U", "1.00", 3, fingerprint),
            tables);

        var catalog = new MobyDexCatalog(new("tests.empty", 1, []));
        await using var runtime = new EditorRuntime(
            mobyPVarResolver: (workspace, entity, index, includeRawData) =>
                UyaMobyPVarEditorService.Describe(workspace, catalog, entity, index, includeRawData),
            mobyPVarCommandExecutor: (workspace, command, token) =>
                UyaMobyPVarEditorService.ExecuteAsync(workspace, command, catalog, token),
            projectOpenHydrator: (workspace, token) =>
                UyaMobyPVarService.HydrateFromSourceAsync(workspace, catalog, token));
        var snapshot = await runtime.OpenAsync(project, TimeSpan.Zero);
        var raw = snapshot.Entities.Single(value => value.EntityId == moby.EntityId).MobyPVar!;
        Equal("none", raw.DatasetId, "raw PVar import does not require a MobyDex entry");
        Equal(true, raw.HasData, "retained gameplay source hydrates legacy project PVar bytes");
        Equal(true, snapshot.IsDirty, "hydrated legacy PVars are offered for project persistence");
        Equal(true, snapshot.Diagnostics.Any(value => value.Code == "moby.pvar-source-hydrated"),
            "raw PVar hydration is visible to the user");

        catalog.Reload(DynamicCatalog().Dataset);
        snapshot = await runtime.GetSnapshotAsync();
        var descriptor = snapshot.Entities.Single(value => value.EntityId == moby.EntityId).MobyPVar!;
        Equal("tests.dynamic", descriptor.DatasetId, "dataset reload overlays the retained raw PVar");
        Equal("7", descriptor.Fields.Single(value => value.Path == "value").Value!.Integer,
            "reloaded schema evaluates existing raw bytes without re-export");

        snapshot = await runtime.ExecuteAsync(PVarCommand(
            moby, descriptor, "value", new(EditorMobyPVarValueKind.Integer, Integer: "42")));
        await runtime.SaveAsync();
        snapshot = await runtime.OpenAsync(project, TimeSpan.Zero);
        Equal(false, snapshot.IsDirty, "reopening does not rewrite an existing PVar overlay");
        Equal("42", snapshot.Entities.Single(value => value.EntityId == moby.EntityId).MobyPVar!
            .Fields.Single(value => value.Path == "value").Value!.Integer,
            "reopening preserves the edited overlay instead of restoring source bytes");
        var reopened = await ForgeProjectWorkspace.OpenAsync(project);
        Equal(42, BinaryPrimitives.ReadInt32LittleEndian(reopened.Content.MobyPVars!.Entries.Single().Data),
            "field overlay edits the persisted raw PVar payload");
        var blockers = new List<string>();
        var rebuilt = UyaMobyPVarService.Rebuild(reopened.Content, tables, blockers);
        Equal(0, blockers.Count, "raw PVar overlay rebuild has no blockers");
        Equal(42, BinaryPrimitives.ReadInt32LittleEndian(rebuilt.Entries.Single().Data),
            "field overlay is baked into the native PVar table");
    }

    private static async Task VerifyStructuredEditorAsync(string root)
    {
        var catalog = EditorCatalog(1);
        var owner = Moby(0x402, 0, -1);
        var target = Moby(0x402, 1, -1);
        var project = Path.Combine(root, "structured-editor");
        var workspace = await ForgeProjectWorkspace.CreateAsync(
            project,
            "Structured PVars",
            new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
            new("UYA", "NTSC-U", "1.00", 3, new string('d', 32)),
            [owner, target]);
        UyaMobyPVarService.CreateDefault(workspace, catalog, owner.EntityId);
        await workspace.SaveAsync();
        await UyaGameplayLayerStore.WriteSourceAsync(project,
            new("UYA", "NTSC-U", "1.00", 3, new string('d', 32)),
            GameplayPvarTableWriter.Write([]));

        await using var runtime = new EditorRuntime(
            mobyPVarResolver: (active, entity, index, includeRawData) =>
                UyaMobyPVarEditorService.Describe(active, catalog, entity, index, includeRawData),
            mobyPVarCommandExecutor: (active, command, token) =>
                UyaMobyPVarEditorService.ExecuteAsync(active, command, catalog, token),
            sourceClassNameResolver: (active, entity) => entity.Source is { } source
                ? active.ResolveMobyDexEntry(catalog, "UYA", source.ClassId)?.Entry.Name
                : null);
        var snapshot = await runtime.OpenAsync(project, TimeSpan.FromMilliseconds(1));
        Equal("Structured editor fixture", snapshot.Entities.Single(value => value.EntityId == owner.EntityId)
            .SourceClassName, "active MobyDex name is exposed without renaming the project entity");
        var nullReference = snapshot.References.Single(value =>
            value.OwnerEntityId == owner.EntityId && value.Reference.FieldKey == "target");
        Equal(false, nullReference.Missing, "configured reference null sentinel is not reported as missing");
        Equal("Built-in dataset tests.editor v1", nullReference.DatasetSource,
            "PVar graph references expose their active MobyDex source");
        var descriptor = snapshot.Entities.Single(value => value.EntityId == owner.EntityId).MobyPVar!;
        Equal("tests.editor", descriptor.DatasetId, "PVar descriptor identifies schema dataset");
        Equal<byte[]?>(null, descriptor.RawData, "unselected entities omit raw PVar bytes from snapshots");
        snapshot = await runtime.ExecuteAsync(new(
            Guid.NewGuid().ToString("D"), EditorCommandKind.SetSelection, [owner.EntityId]));
        descriptor = snapshot.Entities.Single(value => value.EntityId == owner.EntityId).MobyPVar!;
        Equal(32, descriptor.RawData!.Length, "selected moby exposes bounded raw PVar bytes");
        Equal<byte[]?>(null, descriptor.ModifiedByteMask,
            "unchanged PVar omits the modified-byte mask");
        Equal(true, descriptor.Fields.Any(value => value.Kind == EditorMobyPVarFieldKind.Unknown),
            "PVar descriptor exposes unknown byte gaps");
        var settings = descriptor.Fields.Single(value => value.Path == "settings");
        Equal("settings.count", settings.Children!.Single(value => value.Label == "Count").Path,
            "nested struct uses stable field paths");
        Equal(2, descriptor.Fields.Single(value => value.Path == "values").Children!.Count,
            "array fields expand into indexed children");

        var countCommand = PVarCommand(owner, descriptor, "settings.count",
            new(EditorMobyPVarValueKind.Integer, Integer: "42"));
        snapshot = await runtime.ExecuteAsync(countCommand);
        descriptor = snapshot.Entities.Single(value => value.EntityId == owner.EntityId).MobyPVar!;
        Equal(1, ModifiedByteCount(descriptor), "field edit marks only bytes that differ from the baseline");
        Equal(true, IsModified(descriptor, 8), "changed nested field byte is marked");
        await runtime.SaveAsync();
        Equal(42, BinaryPrimitives.ReadInt32LittleEndian((await ForgeProjectWorkspace.OpenAsync(project)).Content
                .MobyPVars!.Entries.Single().Data.AsSpan(8, 4)),
            "autosave persists a bounded nested field patch");
        Equal(true, snapshot.CanUndo, "PVar field edit is one undoable mutation");
        snapshot = await runtime.ExecuteAsync(new(Guid.NewGuid().ToString("D"), EditorCommandKind.Undo, []));
        Equal("0", FindField(snapshot, owner.EntityId, "settings.count").Value!.Integer,
            "undo restores the prior PVar field value");
        Equal<byte[]?>(null, snapshot.Entities.Single(value => value.EntityId == owner.EntityId)
            .MobyPVar!.ModifiedByteMask, "undo clears modified-byte markers restored to baseline");
        snapshot = await runtime.ExecuteAsync(new(Guid.NewGuid().ToString("D"), EditorCommandKind.Redo, []));
        Equal("42", FindField(snapshot, owner.EntityId, "settings.count").Value!.Integer,
            "redo restores the PVar field edit");
        Equal(true, IsModified(snapshot.Entities.Single(value => value.EntityId == owner.EntityId).MobyPVar!, 8),
            "redo restores modified-byte markers");

        var beforeReference = snapshot.Entities.Single(value => value.EntityId == owner.EntityId).MobyPVar!;
        snapshot = await runtime.ExecuteAsync(PVarCommand(owner, beforeReference, "target",
            new(EditorMobyPVarValueKind.Reference, Reference: target.EntityId)));
        var targetField = FindField(snapshot, owner.EntityId, "target");
        Equal(target.EntityId, targetField.Reference!.TargetEntityId,
            "reference edit stores a stable target identity");

        var beforeClear = snapshot.Entities.Single(value => value.EntityId == owner.EntityId).MobyPVar!;
        snapshot = await runtime.ExecuteAsync(PVarCommand(owner, beforeClear, "target",
            new(EditorMobyPVarValueKind.Reference)));
        targetField = FindField(snapshot, owner.EntityId, "target");
        Equal<EntityId?>(null, targetField.Reference!.TargetEntityId,
            "clearing a nullable reference removes the target identity");
        Equal(-1L, targetField.Reference.SourceValue,
            "clearing a nullable reference writes its signed null sentinel");

        var beforeRestore = snapshot.Entities.Single(value => value.EntityId == owner.EntityId).MobyPVar!;
        snapshot = await runtime.ExecuteAsync(PVarCommand(owner, beforeRestore, "target",
            new(EditorMobyPVarValueKind.Reference, Reference: target.EntityId)));

        var beforeColor = snapshot.Entities.Single(value => value.EntityId == owner.EntityId).MobyPVar!;
        snapshot = await runtime.ExecuteAsync(PVarCommand(owner, beforeColor, "tint",
            new(EditorMobyPVarValueKind.Color, Color: [12, 34, 56])));
        Equal(true, FindField(snapshot, owner.EntityId, "tint").Value!.Color!.SequenceEqual(
                new byte[] { 12, 34, 56 }),
            "structured color edits retain exact channels");
        Equal((byte)0, snapshot.Entities.Single(value => value.EntityId == owner.EntityId)
            .MobyPVar!.RawData![27], "structured edits preserve adjacent unknown bytes");
        var beforeArray = snapshot.Entities.Single(value => value.EntityId == owner.EntityId).MobyPVar!;
        snapshot = await runtime.ExecuteAsync(PVarCommand(owner, beforeArray, "values[1]",
            new(EditorMobyPVarValueKind.Integer, Integer: "77")));
        Equal("77", FindField(snapshot, owner.EntityId, "values[1]").Value!.Integer,
            "structured array element edits use their stable indexed path");
        await WaitForRecoveryAsync(runtime, snapshot.LastEventSequence);
        var recovered = await ForgeProjectWorkspace.OpenAsync(project);
        var recovery = (await recovered.ListRecoveriesAsync()).OrderByDescending(value => value.CreatedUnixMilliseconds)
            .First();
        await recovered.LoadRecoveryAsync(recovery.Id);
        Equal(77, BinaryPrimitives.ReadInt32LittleEndian(recovered.Content.MobyPVars!.Entries.Single()
                .Data.AsSpan(16, 4)),
            "PVar edits survive recovery snapshot restore");

        await ThrowsAsync<InvalidOperationException>(() => runtime.ExecuteAsync(countCommand));
        Equal(target.EntityId, FindField(await runtime.GetSnapshotAsync(), owner.EntityId, "target")
            .Reference!.TargetEntityId, "stale blob edits do not partially mutate PVar state");

        var current = (await runtime.GetSnapshotAsync()).Entities.Single(value => value.EntityId == owner.EntityId).MobyPVar!;
        var staleDataset = PVarCommand(owner, current, "settings.count",
            new(EditorMobyPVarValueKind.Integer, Integer: "7"));
        catalog.Reload(EditorCatalog(1, 99).Dataset);
        await ThrowsAsync<InvalidOperationException>(() => runtime.ExecuteAsync(staleDataset));
        Equal("42", FindField(await runtime.GetSnapshotAsync(), owner.EntityId, "settings.count").Value!.Integer,
            "same-version schema replacement rejects a stale schema fingerprint");

        var changedSchema = (await runtime.GetSnapshotAsync()).Entities
            .Single(value => value.EntityId == owner.EntityId).MobyPVar!;
        var changedDataset = PVarCommand(owner, changedSchema, "settings.count",
            new(EditorMobyPVarValueKind.Integer, Integer: "7"));
        catalog.Reload(EditorCatalog(2, 99).Dataset);
        await ThrowsAsync<InvalidOperationException>(() => runtime.ExecuteAsync(changedDataset));
        Equal("42", FindField(await runtime.GetSnapshotAsync(), owner.EntityId, "settings.count").Value!.Integer,
            "dataset version reload rejects stale field commands without changing bytes");

        await runtime.SaveAsync();
        var input = await UyaGameplayLayerStore.CreateBakeInputAsync(project);
        var plan = BakeLayerGraph.CreatePlan(
            new(new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"), "translator-1", "baker-1"),
            [new(BakeLayerId.Mobys, "mobys"u8.ToArray(), [], ReadOnlyMemory<byte>.Empty), input]);
        var staging = await BakeStagingStore.OpenAsync(project);
        var staged = await UyaGameplayLayerStore.StageAsync(
            project, staging, plan.Layers.Single(value => value.Layer == BakeLayerId.Gameplay));
        var output = Path.Combine(staging.RootPath, staged.RelativePath, "pvars");
        var pvarNames = UyaGameplayLayerSchema.SectionNames.Take(4).ToArray();
        var stagedTables = GameplayPvarTableReader.Read(pvarNames.Select((name, index) => new GameplayRawBlock(
            index, 0x58 + index * 4, 0, name, File.ReadAllBytes(Path.Combine(output, $"{name}.bin")))).ToArray(), "UYA")!;
        Equal(1, stagedTables.Entries.Count, "qualified PVar edit stages one native table entry");
        Equal(42, BinaryPrimitives.ReadInt32LittleEndian(stagedTables.Entries[0].Data.AsSpan(8, 4)),
            "qualified primitive edit survives semantic staged re-read");
        Equal(true, stagedTables.Entries[0].Data.AsSpan(24, 3).SequenceEqual(
                new byte[] { 12, 34, 56 }),
            "qualified color edit survives semantic staged re-read");
        Equal(77, BinaryPrimitives.ReadInt32LittleEndian(stagedTables.Entries[0].Data.AsSpan(16, 4)),
            "qualified array edit survives semantic staged re-read");
        Equal((byte)0, stagedTables.Entries[0].Data[27],
            "qualified staging preserves unknown bytes");
        Equal(1, BinaryPrimitives.ReadInt32LittleEndian(stagedTables.Entries[0].Data.AsSpan(20, 4)),
            "qualified reference resolves to the final native moby index");
        Equal(true, stagedTables.RelativePointers.Any(value => value.PvarIndex == 0 && value.Offset == 28),
            "qualified staging preserves declared relative-pointer metadata");

        catalog.Reload(new("tests.empty", 1, []));
        var raw = (await runtime.GetSnapshotAsync()).Entities.Single(value => value.EntityId == owner.EntityId).MobyPVar!;
        Equal(false, raw.Fields.Single().Editable, "schema-less PVar remains inspectable and read-only");
    }

    private static async Task VerifyMissingReferenceAsync(string root, MobyDexCatalog catalog)
    {
        var owner = Moby(0x400, 0, 0);
        var target = Moby(0x400, 1, -1);
        var data = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(data, 7);
        var tables = GameplayPvarTableWriter.Write([new(data, [0], [])]);
        var imported = UyaMobyPVarService.Import([owner, target], tables, catalog)!;
        var reference = imported.Entries.Single().References.Single();
        Equal<EntityId?>(null, reference.Reference.EntityId,
            "unresolved non-null reference retains an explicit missing edge");
        Equal(7, reference.SourceValue, "unresolved reference retains its native value");

        var workspace = await ForgeProjectWorkspace.CreateAsync(
            Path.Combine(root, "missing-reference"),
            "Missing PVar reference",
            new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
            new("UYA", "NTSC-U", "1.00", 3, new string('f', 32)),
            [owner, target],
            null,
            null,
            null,
            imported);
        await using var runtime = new EditorRuntime();
        var snapshot = await runtime.OpenAsync(workspace.RootPath, TimeSpan.Zero);
        Equal(true, snapshot.References.Single().Missing,
            "unresolved non-null reference remains a visible error");
        var blockers = new List<string>();
        var rebuilt = UyaMobyPVarService.Rebuild(workspace.Content, tables, blockers);
        Equal(0, blockers.Count, "unresolved reference can be normalized during bake");
        Equal(-1, BinaryPrimitives.ReadInt32LittleEndian(rebuilt.Entries.Single().Data),
            "bake normalizes an unresolved reference to its null sentinel");
    }

    private static EditorMobyPVarFieldDescriptor FindField(
        EditorSnapshot snapshot,
        EntityId entityId,
        string path)
    {
        var roots = snapshot.Entities.Single(value => value.EntityId == entityId).MobyPVar!.Fields;
        return Flatten(roots).Single(value => value.Path == path);
    }

    private static bool IsModified(EditorMobyPVarDescriptor descriptor, int offset) =>
        descriptor.ModifiedByteMask is { } mask
        && (mask[offset / 8] & 1 << (offset % 8)) != 0;

    private static int ModifiedByteCount(EditorMobyPVarDescriptor descriptor) =>
        Enumerable.Range(0, descriptor.Length).Count(offset => IsModified(descriptor, offset));

    private static IEnumerable<EditorMobyPVarFieldDescriptor> Flatten(
        IEnumerable<EditorMobyPVarFieldDescriptor> fields) => fields.SelectMany(field =>
            new[] { field }.Concat(Flatten(field.Children ?? [])));

    private static EditorCommand PVarCommand(
        ProjectEntity entity,
        EditorMobyPVarDescriptor descriptor,
        string fieldPath,
        EditorMobyPVarValue value) => new(
            Guid.NewGuid().ToString("D"),
            EditorCommandKind.SetMobyPVarField,
            [entity.EntityId],
            MobyPVarEdit: new(
                fieldPath,
                entity.Source!.ClassId,
                descriptor.DatasetId,
                descriptor.DatasetVersion,
                descriptor.SchemaVersion,
                descriptor.SchemaFingerprint,
                descriptor.StateFingerprint,
                value));

    private static MobyDexCatalog EditorCatalog(int version, int countMaximum = 100) => new(new("tests.editor", version,
    [
        new()
        {
            SchemaVersion = MobyDexSchema.CurrentVersion,
            Game = "UYA",
            OClass = 0x402,
            Name = "Structured editor fixture",
            PVar = new()
            {
                Length = 32,
                DefaultHex = "0000000000000000000000000000000000000000ffffffff0000000000000000",
                Relocations = [28],
                Fields =
                [
                    new() { Key = "speed", Label = "Speed", Offset = 0, Length = 4,
                        Definition = new() { Kind = "float32" } },
                    new() { Key = "settings", Label = "Settings", Offset = 4, Length = 8,
                        Definition = new() { Kind = "struct", Fields =
                        [
                            new() { Key = "enabled", Label = "Enabled", Offset = 0, Length = 1,
                                Definition = new() { Kind = "bool8" } },
                            new() { Key = "count", Label = "Count", Offset = 4, Length = 4,
                                Minimum = 0, Maximum = countMaximum, Definition = new() { Kind = "int32" } },
                        ] } },
                    new() { Key = "values", Label = "Values", Offset = 12, Length = 8,
                        Definition = new() { Kind = "array", Count = 2, Stride = 4,
                            Element = new() { Kind = "int32" } } },
                    new() { Key = "target", Label = "Target", Offset = 20, Length = 4,
                        Definition = new() { Kind = "reference", Storage = "int32", Domain = "entity",
                            TargetKind = "moby", NullValue = -1 } },
                    new() { Key = "tint", Label = "Tint", Offset = 24, Length = 3,
                        Definition = new() { Kind = "rgb8" } },
                ],
            },
        },
    ]));

    private static MobyDexCatalog Catalog() => new(new("tests", 1,
    [
        Entry(0x400, "ffffffff7f000000"),
        Entry(0x401, null),
    ]));

    private static MobyDexCatalog DynamicCatalog() => new(new("tests.dynamic", 2,
    [
        new()
        {
            SchemaVersion = MobyDexSchema.CurrentVersion,
            Game = "UYA",
            OClass = 0x499,
            Name = "Dynamic schema fixture",
            PVar = new()
            {
                Length = 8,
                Fields =
                [
                    new()
                    {
                        Key = "value",
                        Label = "Value",
                        Offset = 0,
                        Length = 4,
                        Definition = new() { Kind = "int32" },
                    },
                ],
            },
        },
    ]));

    private static MobyDexEntry Entry(int oClass, string? defaultHex) => new()
    {
        SchemaVersion = MobyDexSchema.CurrentVersion,
        Game = "UYA",
        OClass = oClass,
        Name = $"Test {oClass:X4}",
        PVar = new()
        {
            Length = 8,
            DefaultHex = defaultHex,
            Relocations = [4],
            Fields =
            [
                new()
                {
                    Key = "target",
                    Label = "Target",
                    Offset = 0,
                    Length = 4,
                    Definition = new()
                    {
                        Kind = "reference",
                        Storage = "int32",
                        Domain = "entity",
                        TargetKind = "moby",
                        NullValue = -1,
                    },
                },
            ],
        },
    };

    private static ProjectEntity Moby(int oClass, int sourceIndex, int pvarIndex)
    {
        var raw = new byte[UyaMobyInstancesReader.RecordSize];
        BinaryPrimitives.WriteInt32LittleEndian(raw, UyaMobyInstancesReader.RecordSize);
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(0x10), 0x1234 + sourceIndex);
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(0x28), oClass);
        BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(0x2c), 1);
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(0x68), pvarIndex);
        return new(
            EntityId.New(),
            $"Moby {sourceIndex}",
            "mobys",
            ProjectTransform.Identity,
            null,
            new("UYA", 3, "gameplay/core/moby_instances", sourceIndex),
            Source: new(oClass, raw, true));
    }

    private static ProjectEntity Collision(int payloadIndex) => new(
        EntityId.New(),
        $"Collision chunk {payloadIndex}",
        "collision",
        ProjectTransform.Identity,
        null,
        new("UYA", 3, $"collision/chunk-{payloadIndex}", 0),
        Collision: new(ProjectCollisionPieceKind.Solid, payloadIndex, 0, 0, 0, []));

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

    private static async Task WaitForRecoveryAsync(EditorRuntime runtime, long afterSequence)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if ((await runtime.ReadEventsAsync(afterSequence, 100))
                .Any(value => value.Kind == EditorEventKind.RecoveryWritten)) return;
            await Task.Delay(10);
        }
        throw new InvalidOperationException("PVar edit did not write recovery state.");
    }

    private static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected {expected}, got {actual}");
    }
}
