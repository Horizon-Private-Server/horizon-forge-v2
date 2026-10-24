using Forge.Host.Domain;

internal static class EditorSelectionTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-selection-{Guid.NewGuid():N}");
        var first = EntityId.New();
        var second = EntityId.New();
        var calls = new List<(EntityId Id, bool Raw)>();
        await using var runtime = new EditorRuntime(mobyPVarResolver: (_, entity, _, raw) =>
        {
            calls.Add((entity.EntityId, raw));
            return new("test", 1, 1, "Test", "schema", "state", 1, true, false, null,
                raw ? [42] : null, raw ? [0] : null, []);
        });
        try
        {
            await ForgeProjectWorkspace.CreateAsync(root, "Selection",
                new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
                new("UYA", "NTSC-U", "1.00", 3, new string('a', 32)),
                [new(first, "First", "mobys", ProjectTransform.Identity, null, null),
                 new(second, "Second", "mobys", ProjectTransform.Identity, null, null)]);
            await runtime.OpenAsync(root, TimeSpan.Zero);
            calls.Clear();
            var selected = await Select([first]);
            Check(selected.Kind == EditorCommandResultKind.Selection && selected.Snapshot is null,
                "selection uses a compact result");
            Check(selected.Selection!.EntityIds.SequenceEqual([first]), "selection order");
            Check(selected.Selection.MobyPVars.Single().MobyPVar!.RawData!.SequenceEqual(new byte[] { 42 }),
                "selected entity includes raw PVars");
            Check(calls.SequenceEqual([(first, true)]), "only changed entities are resolved");
            calls.Clear();
            selected = await Select([first, second]);
            Check(calls.SequenceEqual([(second, true)]), "retained selection is not rebuilt");
            Check(selected.Selection!.EntityIds.SequenceEqual([first, second]), "multiselection");
            calls.Clear();
            selected = await Select([second]);
            Check(calls.SequenceEqual([(first, false)]), "deselected entity loses raw data");
            Check(selected.Selection!.MobyPVars.Single().MobyPVar!.RawData is null, "raw data cleared");
            calls.Clear();
            selected = await Select([second]);
            Check(calls.Count == 0 && selected.Selection!.MobyPVars.Count == 0, "unchanged selection reuses properties");
            selected = await Select([]);
            Check(selected.Selection!.EntityIds.Count == 0, "clear selection");
            Check(!selected.IsDirty && !selected.CanUndo && !selected.CanRedo, "selection preserves clean history");
            var sequence = selected.LastEventSequence;
            try
            {
                await Select([EntityId.New()]);
                throw new InvalidOperationException("Unknown entity selection was accepted");
            }
            catch (ArgumentException) { }
            var snapshot = await runtime.GetSnapshotAsync();
            Check(snapshot.Selection.Count == 0 && snapshot.LastEventSequence == sequence,
                "rejected selection leaves state and event sequence intact");
        }
        finally
        {
            await runtime.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        Task<EditorCommandResult> Select(EntityId[] ids) => runtime.ExecuteResultAsync(
            new(Guid.NewGuid().ToString("D"), EditorCommandKind.SetSelection, ids));
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
