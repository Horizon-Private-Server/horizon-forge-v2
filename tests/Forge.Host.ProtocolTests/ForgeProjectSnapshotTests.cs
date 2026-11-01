using Forge.Host.Domain;

internal static class ForgeProjectSnapshotTests
{
    internal static async Task RunAsync()
    {
        var root = Directory.CreateTempSubdirectory("forge-project-snapshot-tests-").FullName;
        try
        {
            var project = await ForgeProjectWorkspace.CreateAsync(Path.Combine(root, "project"), "Snapshot",
                new("DL", "NTSC-U", "1.00", "dl-ntsc-u"), new("DL", "NTSC-U", "1.00", 3, new string('a', 32)), []);
            var bytes = new byte[] { 1, 2, 3, 4 };
            var asset = await project.AttachAssetAsync(AssetKind.World, 1, bytes);
            await project.SaveAsync();
            var saved = await File.ReadAllBytesAsync(project.ContentFilePath);
            Directory.CreateDirectory(Path.Combine(project.RootPath, "content", "references"));
            await File.WriteAllBytesAsync(Path.Combine(project.RootPath, "content", "references", "input.bin"), bytes);
            await File.WriteAllBytesAsync(Path.Combine(project.RootPath, "content", "empty.bin"), []);
            Directory.CreateDirectory(Path.Combine(project.RootPath, "cache"));
            await File.WriteAllTextAsync(Path.Combine(project.RootPath, "cache", "original.txt"), "Preserve me");
            project.Rename("Unsaved snapshot");
            project.SetOcclusionCoverage(new(1, false, []));
            var fingerprint = project.CurrentFingerprint;
            var temporaries = TemporaryRoots();
            string path;
            using (var snapshot = await ForgeProjectSnapshot.CreateAsync(project, project.Content, default))
            {
                path = snapshot.RootPath;
                var copy = await ForgeProjectWorkspace.OpenAsync(path);
                Check(copy.Manifest.Target.Game == "DL" && copy.CurrentFingerprint == fingerprint && !copy.IsDirty,
                    "Shared snapshots preserve captured game-neutral metadata and unsaved state.");
                Check(!copy.Content.OcclusionCoverage.PrimaryEnabled,
                    "Shared snapshots preserve caller-supplied content without an occlusion policy.");
                var attached = await copy.ReadAttachedAssetVerifiedAsync(new(asset.Id, asset.Kind), 1024);
                var reference = await File.ReadAllBytesAsync(Path.Combine(path, "content", "references", "input.bin"));
                Check(bytes.SequenceEqual(attached) && bytes.SequenceEqual(reference)
                    && new FileInfo(Path.Combine(path, "content", "empty.bin")).Length == 0,
                    "Snapshots copy verified attachments, extra content and empty files.");
                Check(!Directory.Exists(Path.Combine(path, "cache")), "Project caches are not copied into snapshots.");
            }
            Check(!Directory.Exists(path), "Disposal removes a completed temporary project.");
            try
            {
                using var snapshot = await ForgeProjectSnapshot.CreateAsync(project, project.Content, default);
                path = snapshot.RootPath;
                throw new IOException("Fixture consumer failed.");
            }
            catch (IOException) { Check(!Directory.Exists(path), "Consumer failure still disposes the temporary project."); }
            await ThrowsAsync<OperationCanceledException>(() => ForgeProjectSnapshot.CreateAsync(project, project.Content, new(true)));
            var assetPath = project.ResolveAttachedAssetPath(asset.Id)!;
            await File.WriteAllBytesAsync(assetPath, new byte[bytes.Length]);
            try { await ThrowsAsync<InvalidDataException>(() => ForgeProjectSnapshot.CreateAsync(project, project.Content, default)); }
            finally { await File.WriteAllBytesAsync(assetPath, bytes); }
            Check(temporaries.SetEquals(TemporaryRoots()), "Cancellation and integrity failures leave no partial snapshots.");
            var after = await File.ReadAllBytesAsync(project.ContentFilePath);
            Check(saved.SequenceEqual(after) && project.CurrentFingerprint == fingerprint && project.IsDirty
                && await File.ReadAllTextAsync(Path.Combine(project.RootPath, "cache", "original.txt")) == "Preserve me",
                "Snapshot creation never saves edits or changes original caches.");
        }
        finally { Directory.Delete(root, true); }
    }

    private static HashSet<string> TemporaryRoots() => Directory.GetDirectories(Path.GetTempPath(), "forge-project-snapshot-*").ToHashSet();
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}."); }
}
