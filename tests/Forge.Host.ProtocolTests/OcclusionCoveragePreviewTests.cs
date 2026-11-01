using Forge.Host.Domain;

internal static class OcclusionCoveragePreviewTests
{
    internal static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-coverage-preview-{Guid.NewGuid():N}");
        try
        {
            await ForgeProjectWorkspace.CreateAsync(root, "Coverage preview", new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
                new("UYA", "NTSC-U", "1.00", 3, new string('a', 32)), []);
            await using var unavailable = new EditorRuntime();
            Check(!unavailable.HasCapability("editor.occlusion-coverage.preview"), "Pinned builds must not advertise an unavailable preview adapter.");
            await ThrowsAsync<InvalidOperationException>(() => unavailable.PreviewOcclusionCoverageAsync(new string('a', 64), ""));
            var catalog = await AssetCatalogStore.OpenAsync(Path.Combine(root, "catalog"));
            var entered = Signal(); var release = Signal(); ForgeProjectWorkspace? captured = null; string? capturedCatalog = null;
            await using var runtime = new EditorRuntime(occlusionCoveragePreviewExecutor: async (projection, catalogRoot, _, progress, token) =>
            {
                captured = projection; capturedCatalog = catalogRoot; entered.TrySetResult(true);
                await release.Task.WaitAsync(token);
                if (progress is not null) await progress(new(1, 1));
                return new(2, 4, new string('c', 64), new string('d', 64), 0, 0, [], []);
            });
            var opened = await runtime.OpenAsync(root, catalog.RootPath, TimeSpan.Zero);
            Check(opened.Capabilities.Contains("editor.occlusion-coverage.preview"), "An installed adapter must advertise preview support.");
            var initial = await runtime.QueryOcclusionCoverageAsync();
            var pending = runtime.PreviewOcclusionCoverageAsync(initial.ProjectFingerprint, "");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(capturedCatalog == catalog.RootPath, "Preview jobs must capture the active catalog context under the editor gate.");
            var live = await runtime.GetSnapshotAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Check(live.LastEventSequence == opened.LastEventSequence, "Preview must leave the editor gate free without history or dirty events.");
            await ThrowsAsync<InvalidOperationException>(() => runtime.PreviewOcclusionCoverageAsync(initial.ProjectFingerprint, ""));
            await runtime.UpdateOcclusionCoverageAsync(initial.ProjectFingerprint, initial.State with { PrimaryEnabled = false });
            Check(captured!.Content.OcclusionCoverage.PrimaryEnabled, "The job must retain its captured recipe after edits.");
            release.TrySetResult(true); await ThrowsAsync<InvalidOperationException>(async () => await pending);
            var current = await runtime.QueryOcclusionCoverageAsync();
            await ThrowsAsync<InvalidOperationException>(() => runtime.PreviewOcclusionCoverageAsync(initial.ProjectFingerprint, ""));

            Reset(); var before = await runtime.GetSnapshotAsync();
            var success = runtime.PreviewOcclusionCoverageAsync(current.ProjectFingerprint, "");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); release.TrySetResult(true);
            var result = await success;
            Check(result.ProjectFingerprint == current.ProjectFingerprint && result.RecipeFingerprint == current.RecipeFingerprint,
                "Successful preview must identify the captured project and recipe.");
            var after = await runtime.GetSnapshotAsync();
            Check(before.LastEventSequence == after.LastEventSequence && before.IsDirty == after.IsDirty && before.CanUndo == after.CanUndo,
                "Successful preview cannot alter dirty state, history or accepted project events.");

            Reset(); var aba = runtime.PreviewOcclusionCoverageAsync(current.ProjectFingerprint, "");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await runtime.UpdateOcclusionCoverageAsync(current.ProjectFingerprint, current.State with { PrimaryEnabled = true });
            await runtime.ExecuteAsync(new(Guid.NewGuid().ToString("D"), EditorCommandKind.Undo, []));
            Check((await runtime.QueryOcclusionCoverageAsync()).ProjectFingerprint == current.ProjectFingerprint,
                "Undo must restore the original fingerprint for the ABA stale-result fixture.");
            release.TrySetResult(true); await ThrowsAsync<InvalidOperationException>(async () => await aba);

            Reset(); using var cancellation = new CancellationTokenSource();
            var cancelled = runtime.PreviewOcclusionCoverageAsync(current.ProjectFingerprint, "", cancellationToken: cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
            await ThrowsAsync<OperationCanceledException>(async () => await cancelled);
            await ThrowsAsync<OperationCanceledException>(() => runtime.PreviewOcclusionCoverageAsync(current.ProjectFingerprint, "", cancellationToken: cancellation.Token));

            await runtime.SaveAsync(); current = await runtime.QueryOcclusionCoverageAsync();
            Reset(); var reopenedJob = runtime.PreviewOcclusionCoverageAsync(current.ProjectFingerprint, "");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await runtime.CloseAsync(); await runtime.OpenAsync(root, catalog.RootPath, TimeSpan.Zero);
            release.TrySetResult(true); await ThrowsAsync<InvalidOperationException>(async () => await reopenedJob);
            Check((await runtime.QueryOcclusionCoverageAsync()).ProjectFingerprint == current.ProjectFingerprint,
                "Closing and reopening the same unchanged project still rejects its old in-flight result.");
            void Reset() { entered = Signal(); release = Signal(); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}.");
    }
}
