namespace Forge.Host.Domain;

/// <summary>Owns a disposable on-disk copy of captured project inputs, excluding caches and staging.</summary>
internal sealed class ForgeProjectSnapshot : IDisposable
{
    private const long MaximumInputBytes = 2L * 1024 * 1024 * 1024;
    private const int MaximumEntries = 50_000;

    private ForgeProjectSnapshot(string rootPath) => RootPath = rootPath;

    internal string RootPath { get; }

    internal static async Task<ForgeProjectSnapshot> CreateAsync(
        ForgeProjectWorkspace projection, ForgeProjectContent content, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(content);
        token.ThrowIfCancellationRequested();
        var root = Directory.CreateTempSubdirectory("forge-project-snapshot-").FullName;
        try
        {
            var bytes = 0L; var entries = 0; var buffer = new byte[64 * 1024];
            var contentRoot = Path.Combine(projection.RootPath, "content");
            var originalContent = projection.ContentFilePath;
            var defaultContent = ForgeProjectPersistence.ResolveRelativePath(projection.RootPath, ForgeProjectWorkspace.DefaultContentPath);
            RejectLink(projection.RootPath);
            if (Directory.Exists(contentRoot)) await CopyDirectoryAsync(contentRoot, Path.Combine(root, "content"), 0);
            foreach (var asset in content.Assets)
            {
                token.ThrowIfCancellationRequested(); Budget(asset.Size);
                var assetId = asset.Id.ToString();
                RejectLink(Path.Combine(projection.RootPath, "assets"));
                RejectLink(Path.Combine(projection.RootPath, "assets", assetId[..2]));
                RejectLink(Path.Combine(projection.RootPath, "assets", assetId[..2], $"{assetId}.blob"));
                var value = await projection.ReadAttachedAssetVerifiedAsync(new(asset.Id, asset.Kind),
                    ForgeProjectWorkspace.MaxAttachedAssetBytes, token);
                await ForgeProjectPersistence.WriteFileSafelyAsync(Path.Combine(root, "assets", assetId[..2], $"{assetId}.blob"), value, token);
            }
            var manifest = projection.Manifest with { Content = ForgeProjectWorkspace.DefaultContentPath };
            await ForgeProjectPersistence.SaveAsync(root, manifest, content, token);
            token.ThrowIfCancellationRequested();
            return new(root);

            void Budget(long size)
            {
                if (size < 0 || size > ForgeProjectWorkspace.MaxAttachedAssetBytes || ++entries > MaximumEntries
                    || size > MaximumInputBytes - bytes)
                    throw new InvalidDataException("Project snapshot exceeds its 2 GiB/50,000-entry input budget. Reduce project inputs and retry.");
                bytes += size;
            }

            async Task CopyDirectoryAsync(string source, string destination, int depth)
            {
                token.ThrowIfCancellationRequested(); RejectLink(source);
                if (depth > 16) throw new InvalidDataException("Project snapshot content exceeds its directory-depth limit.");
                Directory.CreateDirectory(destination);
                foreach (var path in Directory.EnumerateFileSystemEntries(source))
                {
                    token.ThrowIfCancellationRequested(); RejectLink(path);
                    var target = Path.Combine(destination, Path.GetFileName(path));
                    if (Directory.Exists(path)) { Budget(0); await CopyDirectoryAsync(path, target, depth + 1); continue; }
                    var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                    if (string.Equals(path, originalContent, comparison) || string.Equals(path, defaultContent, comparison)) continue;
                    var info = new FileInfo(path); Budget(info.Length);
                    await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous);
                    if (info.Length == 0) continue;
                    await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
                    long copied = 0; int count;
                    while ((count = await input.ReadAsync(buffer, token)) > 0)
                    {
                        copied += count;
                        if (copied > info.Length) throw new InvalidDataException("Project input changed while being captured. Retry the snapshot.");
                        await output.WriteAsync(buffer.AsMemory(0, count), token);
                    }
                    if (copied != info.Length) throw new InvalidDataException("Project input changed while being captured. Retry the snapshot.");
                }
            }
        }
        catch
        {
            Directory.Delete(root, recursive: true);
            throw;
        }
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw new InvalidDataException("Project snapshot inputs cannot contain symbolic links or device files.");
    }

    public void Dispose()
    {
        if (Directory.Exists(RootPath)) Directory.Delete(RootPath, recursive: true);
    }
}
