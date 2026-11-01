using System.Security.Cryptography;
using Forge.Host.Domain;
using Forge.Host.Games.UYA;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.Textures.Palettes;
using RatchetPs2.Core.Textures.Pif;
using RatchetPs2.Games.UYA.Gameplay;
using RatchetPs2.Games.UYA.Level;
using RatchetPs2.Sdk;

namespace Forge.Host.ProtocolTests.Games.UYA;

internal static class UyaOcclusionSceneSnapshotTests
{
    internal static async Task RunAsync()
    {
        var root = Directory.CreateTempSubdirectory("forge-scene-snapshot-tests-").FullName;
        try
        {
            var catalog = await AssetCatalogStore.OpenAsync(Path.Combine(root, "catalog"));
            foreach (var (kind, classId) in new[] { (AssetKind.Moby, 100), (AssetKind.Tie, 200), (AssetKind.Shrub, 300) })
                await catalog.PutAsync(kind, UyaAssetImportService.CanonicalFormatVersion, UyaProjectTests.CanonicalAsset(kind, 0x22),
                    new("fixture", new("UYA", "NTSC-U", "1.00", "level03", "level_wad/assets/asset_wad.bin", 0, new string('a', 32)),
                        [$"{kind.ToString().ToLowerInvariant()}:{classId}"], ["vanilla", "game:UYA", "level:03"]));
            var projectRoot = Path.Combine(root, "project"); var iso = UyaProjectTests.CreateIso();
            await UyaProjectService.CreateValidatedAsync(new MemoryStream(iso, writable: false), catalog,
                new("fixture.iso", catalog.RootPath, projectRoot, "Snapshot", new string('a', 32), "1.00", 3, true));
            var project = await ForgeProjectWorkspace.OpenAsync(projectRoot);
            var source = LevelArchiveReader.ExtractPrimary(GameId.UYA, new MemoryStream(iso, writable: false), 3);
            var context = new BakeFingerprintContext(project.Manifest.Target, "snapshot-test", "snapshot-test");
            Check((await UyaBakeService.BakeAsync(projectRoot, catalog, context)).Succeeded, "The source fixture must have last-known-good staging.");
            var saved = project.CurrentFingerprint;
            await File.WriteAllBytesAsync(Path.Combine(projectRoot, "content", "snapshot-empty-input.bin"), []);
            var before = await FilesAsync(projectRoot); var catalogBefore = await FilesAsync(catalog.RootPath);
            var temporaries = TemporaryRoots();
            var tie = project.Content.Entities.Single(e => e.Layer == "ties");
            var moved = tie.Transform with { Position = new(72, 84, 96) };
            project.UpdateTransform(tie.EntityId, moved);
            project.SetOcclusionCoverage(new(1, false,
                [new(EntityId.New(), "Graph", true, ProjectOcclusionCoverageRole.Include,
                    ProjectTransform.Identity with { Position = new(40, 40, 20) }, Graph: new(new(32, 32, 32), [new(EntityId.New(), "Seed", new(0, 0, 0))]))]));
            var current = project.CurrentFingerprint;
            Check(project.IsDirty && current != saved, "Scene input must include an unsaved editor state.");
            var progress = new List<IsoProgress>();
            var input = await UyaOcclusionSceneSnapshot.CaptureAsync(project.CreateProjection(), catalog.RootPath, source,
                p => { progress.Add(p); return ValueTask.CompletedTask; }, default);
            var package = UyaLevelWadUnpacker.Unpack(input.LevelWad);
            var ties = UyaTieInstancesReader.Read(package.Files.Single(f => f.Path == "gameplay/core/tie_instances.bin").Bytes);
            Check(ties.Count == 1 && Math.Abs(ties.Instances.Single().Transform.Position.X - 72) < .0001f
                && Math.Abs(ties.Instances.Single().Transform.Position.Y - 84) < .0001f && Math.Abs(ties.Instances.Single().Transform.Position.Z - 96) < .0001f,
                "The packed scene must contain the unsaved native tie pose.");
            Check(input.ProjectFingerprint == current && input.SceneFingerprint == Convert.ToHexStringLower(SHA256.HashData(input.LevelWad)),
                "Scene identity must describe the exact packed input and captured project.");
            Check(progress[0] == new IsoProgress(0, 3_000) && progress[^1] == new IsoProgress(3_000, 3_000), "Snapshot progress must cover preparation through composition.");
            Check(project.CurrentFingerprint == current && project.IsDirty && project.Content.OcclusionCoverage.RequiresGeneration,
                "Scratch source-mode preflight must not change coverage or dirty state.");
            Check(Equal(before, await FilesAsync(projectRoot)) && Equal(catalogBefore, await FilesAsync(catalog.RootPath)),
                "Snapshot must not write project, original staging, recovery, generated occlusion or catalog files.");
            Check((await ForgeProjectWorkspace.OpenAsync(projectRoot)).CurrentFingerprint == saved, "Snapshot must not implicitly save edits.");
            var repeat = await UyaOcclusionSceneSnapshot.CaptureAsync(project.CreateProjection(), catalog.RootPath, source, null, default);
            Check(repeat.SceneFingerprint == input.SceneFingerprint, "Identical captured inputs must compose identically.");
            project.UpdateTransform(tie.EntityId, moved with { Position = new(76, 84, 96) });
            var changed = await UyaOcclusionSceneSnapshot.CaptureAsync(project.CreateProjection(), catalog.RootPath, source, null, default);
            Check(changed.SceneFingerprint != input.SceneFingerprint, "An unsaved scene edit must invalidate the packed scene identity.");
            using var cancellation = new CancellationTokenSource();
            await ThrowsAsync<OperationCanceledException>(() => UyaOcclusionSceneSnapshot.CaptureAsync(project.CreateProjection(), catalog.RootPath, source,
                p => { if (p.Completed >= 1_000) cancellation.Cancel(); return ValueTask.CompletedTask; }, cancellation.Token));
            await ThrowsAsync<InvalidDataException>(() => UyaOcclusionSceneSnapshot.CaptureAsync(project.CreateProjection(), catalog.RootPath, new byte[] { 1 }, null, default));
            if (!OperatingSystem.IsWindows())
            {
                var link = Path.Combine(projectRoot, "content", "snapshot-link");
                File.CreateSymbolicLink(link, Path.Combine(projectRoot, ForgeProjectWorkspace.ManifestFileName));
                await ThrowsAsync<InvalidDataException>(() => UyaOcclusionSceneSnapshot.CaptureAsync(project.CreateProjection(), catalog.RootPath, source, null, default));
                File.Delete(link);
            }
            var oversized = Path.Combine(projectRoot, "content", "snapshot-too-large.bin");
            using (var file = new FileStream(oversized, FileMode.CreateNew)) file.SetLength(ForgeProjectWorkspace.MaxAttachedAssetBytes + 1);
            await ThrowsAsync<InvalidDataException>(() => UyaOcclusionSceneSnapshot.CaptureAsync(project.CreateProjection(), catalog.RootPath, source, null, default));
            File.Delete(oversized);
            await ThrowsAsync<IOException>(() => UyaOcclusionSceneSnapshot.CaptureAsync(project.CreateProjection(), catalog.RootPath, source,
                _ => throw new IOException("Fixture callback failed."), default));
            Check(temporaries.SetEquals(TemporaryRoots()), "Success, cancellation and failures must clean all temporary workspaces.");
            Check(Equal(before, await FilesAsync(projectRoot)), "Rejected snapshots must retain every original project file.");
            var pif = PifWriter.Write(PifWriter.CreateIndexed8(8, 8, new byte[1024], new byte[64]));
            var canonical = UyaCanonicalAssetCodec.Encode(new byte[0x20], [0x44], [new(0, pif)]);
            var asset = await project.AttachAssetAsync(AssetKind.Tie, UyaAssetImportService.CanonicalFormatVersion,
                canonical, tie.Asset!.Id);
            project.SetAssetOverride(tie.Asset!, new(asset.Id, asset.Kind));
            var attachedBefore = await FilesAsync(projectRoot);
            var replacement = await UyaOcclusionSceneSnapshot.CaptureAsync(project.CreateProjection(), catalog.RootPath, source, null, default);
            var packedAssets = UyaLevelPackService.IncludeSourceStaticAssets(replacement.LevelWad, []);
            Check(packedAssets.Single(a => a.Family == TextureAssetFamily.Tie).ModelBytes.Span[0] == 0x44,
                "Unsaved attached model overrides must reach the packed scene instead of base geometry.");
            Check(replacement.SceneFingerprint != changed.SceneFingerprint && Equal(attachedBefore, await FilesAsync(projectRoot)),
                "Attached geometry invalidates scene identity without rewriting original inputs.");
            project.RemoveEntity(tie.EntityId);
            var deleted = await UyaOcclusionSceneSnapshot.CaptureAsync(project.CreateProjection(), catalog.RootPath, source, null, default);
            var deletedFiles = UyaLevelWadUnpacker.Unpack(deleted.LevelWad).Files;
            Check(UyaTieInstancesReader.Read(deletedFiles.Single(f => f.Path == "gameplay/core/tie_instances.bin").Bytes).Count == 0,
                "Unsaved deletion must remove the blocker instance from the packed scene.");
            var assetPath = project.ResolveAttachedAssetPath(asset.Id)!;
            var validBytes = await File.ReadAllBytesAsync(assetPath);
            await File.WriteAllBytesAsync(assetPath, new byte[validBytes.Length]);
            try { await ThrowsAsync<InvalidDataException>(() => UyaOcclusionSceneSnapshot.CaptureAsync(project.CreateProjection(), catalog.RootPath, source, null, default)); }
            finally { await File.WriteAllBytesAsync(assetPath, validBytes); }
            Check(temporaries.SetEquals(TemporaryRoots()) && Equal(attachedBefore, await FilesAsync(projectRoot)),
                "Custom asset evaluation and integrity failures must also clean snapshots and preserve input files.");
        }
        finally { Directory.Delete(root, true); }
    }

    private static HashSet<string> TemporaryRoots() => Directory.GetDirectories(Path.GetTempPath(), "forge-project-snapshot-*").ToHashSet();
    private static async Task<Dictionary<string, string>> FilesAsync(string root)
    {
        var files = new Dictionary<string, string>();
        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            files.Add(Path.GetRelativePath(root, file), Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(file))));
        return files;
    }
    private static bool Equal(Dictionary<string, string> a, Dictionary<string, string> b) => a.Count == b.Count && a.All(p => b.GetValueOrDefault(p.Key) == p.Value);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}."); }
}
