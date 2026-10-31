using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace Forge.Updater;

internal static partial class PackageVerifier
{
    private const long MaximumManifestBytes = 8 * 1024 * 1024;
    private const int MaximumFiles = 10_000;

    public static async Task<PackageManifest> VerifyAsync(
        string root,
        string expectedManifestSha256,
        string? expectedVersion = null,
        CancellationToken cancellationToken = default,
        bool allowExtraFiles = false)
    {
        root = Path.GetFullPath(root);
        AssertDirectDirectory(root, "Package root");
        var manifestPath = Path.Combine(root, "forge-package.json");
        var manifestInfo = new FileInfo(manifestPath);
        if (!manifestInfo.Exists || IsLink(manifestInfo) || manifestInfo.Length is <= 0 or > MaximumManifestBytes)
            throw new InvalidDataException("Package manifest is missing or invalid.");

        var manifestHash = await HashFileAsync(manifestPath, cancellationToken);
        if (!FixedEquals(manifestHash, expectedManifestSha256))
            throw new InvalidDataException("Package manifest fingerprint changed.");
        var bytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
        var manifest = JsonSerializer.Deserialize(bytes, UpdaterJsonContext.Default.PackageManifest)
            ?? throw new InvalidDataException("Package manifest is invalid.");
        ValidateManifest(manifest, expectedVersion);

        var actual = EnumerateFiles(root);
        var expected = manifest.Files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
        // Old overlay installs may contain leftovers; stages and candidates must still match exactly.
        if (allowExtraFiles)
        {
            var paths = expected.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
            actual.RemoveAll(file => !paths.Contains(file.RelativePath));
        }
        if (actual.Count != manifest.FileCount || actual.Count != expected.Length)
            throw new InvalidDataException("Package contains extra or missing files.");
        long expandedSize = 0;
        for (var index = 0; index < expected.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = expected[index];
            var file = actual[index];
            if (!string.Equals(entry.Path, file.RelativePath, StringComparison.Ordinal)
                || entry.Size != file.Info.Length
                || !FixedEquals(await HashFileAsync(file.Info.FullName, cancellationToken), entry.Sha256))
                throw new InvalidDataException($"Package file does not match its manifest: {entry.Path}");
            expandedSize = checked(expandedSize + file.Info.Length);
        }
        if (expandedSize != manifest.ExpandedSize)
            throw new InvalidDataException("Package expanded size does not match its manifest.");
        ValidateExecutableModes(root, manifest);
        return manifest;
    }

    public static async Task CopyAsync(
        string source,
        string destination,
        CancellationToken cancellationToken = default)
    {
        AssertDirectDirectory(source, "Staged package");
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException("Update candidate already exists.");
        Directory.CreateDirectory(destination);
        var entries = Walk(source);
        foreach (var directory in entries.Where(entry => entry.IsDirectory)
            .OrderBy(entry => entry.RelativePath.Count(character => character == '/')))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(destination,
                directory.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        }
        foreach (var file in entries.Where(entry => !entry.IsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.Combine(destination, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file.AbsolutePath, target, false);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, File.GetUnixFileMode(file.AbsolutePath));
        }
        await Task.CompletedTask;
    }

    public static string ManifestFingerprint(string root)
    {
        var path = Path.Combine(root, "forge-package.json");
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    }

    public static void AssertSafeRemovalRoot(string path)
    {
        if (!Directory.Exists(path)) return;
        var info = new DirectoryInfo(path);
        if (IsLink(info)) throw new InvalidDataException("Updater-owned directory is indirect.");
        _ = Walk(path);
    }

    private static void ValidateManifest(PackageManifest manifest, string? expectedVersion)
    {
        if (manifest.SchemaVersion != 1 || manifest.Product != "horizon-forge"
            || manifest.Platform is not ("linux" or "windows")
            || manifest.FileCount < 1 || manifest.FileCount > MaximumFiles
            || manifest.Files.Count != manifest.FileCount || manifest.ExpandedSize < 0
            || string.IsNullOrWhiteSpace(manifest.Executable)
            || (expectedVersion is not null && manifest.Version != expectedVersion))
            throw new InvalidDataException("Package manifest identity is invalid.");
        var comparer = StringComparer.OrdinalIgnoreCase;
        var paths = new HashSet<string>(comparer);
        foreach (var entry in manifest.Files)
        {
            ValidateRelativePath(entry.Path);
            if (entry.Size < 0 || !Sha256Pattern().IsMatch(entry.Sha256) || !paths.Add(entry.Path))
                throw new InvalidDataException("Package file manifest is invalid.");
        }
        ValidateRelativePath(manifest.Executable);
        if (!paths.Contains(manifest.Executable)) throw new InvalidDataException("Package executable is not manifested.");
    }

    private static List<(string RelativePath, FileInfo Info)> EnumerateFiles(string root)
    {
        var result = new List<(string RelativePath, FileInfo Info)>();
        foreach (var entry in Walk(root))
        {
            if (entry.IsDirectory) continue;
            var info = new FileInfo(entry.AbsolutePath);
            if (!info.Exists) throw new InvalidDataException("Package contains a special file.");
            if (entry.RelativePath != "forge-package.json") result.Add((entry.RelativePath, info));
            if (result.Count > MaximumFiles) throw new InvalidDataException("Package contains too many files.");
        }
        result.Sort((left, right) => StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath));
        return result;
    }

    private static void ValidateExecutableModes(string root, PackageManifest manifest)
    {
        if (manifest.Platform != "linux" || !OperatingSystem.IsLinux()) return;
        var expected = manifest.ExecutablePaths.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var actual = new List<string>();
        foreach (var file in manifest.Files)
        {
            var mode = File.GetUnixFileMode(Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar)));
            if ((mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
                actual.Add(file.Path);
        }
        actual.Sort(StringComparer.Ordinal);
        if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
            throw new InvalidDataException("Package executable permissions do not match its manifest.");
    }

    private static void ValidateRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Contains('\\') || value.Contains(':')
            || Path.IsPathRooted(value) || value.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("Package path is unsafe.");
    }

    private static void AssertDirectDirectory(string path, string label)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists) throw new InvalidDataException($"{label} is missing or indirect.");
        for (var current = info; current is not null; current = current.Parent)
            if (IsLink(current)) throw new InvalidDataException($"{label} is missing or indirect.");
    }

    private static bool IsLink(FileSystemInfo info) =>
        info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0;

    private static List<WalkEntry> Walk(string root)
    {
        var result = new List<WalkEntry>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Package contains a link.");
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
                result.Add(new WalkEntry(path, relative, isDirectory));
                if (isDirectory) pending.Push(path);
            }
        }
        return result;
    }

    private sealed record WalkEntry(string AbsolutePath, string RelativePath, bool IsDirectory);

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static bool FixedEquals(string left, string right) =>
        left.Length == right.Length && CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(left), Convert.FromHexString(right));

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();
}
