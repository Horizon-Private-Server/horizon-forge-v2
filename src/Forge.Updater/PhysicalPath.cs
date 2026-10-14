namespace Forge.Updater;

internal static class PhysicalPath
{
    public static bool Equals(string left, string right) => Resolve(left).Equals(Resolve(right), PathComparison);

    public static bool IsWithin(string parent, string candidate)
    {
        var relative = Path.GetRelativePath(Resolve(parent), Resolve(candidate));
        return relative == "." || (!relative.StartsWith($"..{Path.DirectorySeparatorChar}", PathComparison)
            && relative != ".." && !Path.IsPathFullyQualified(relative));
    }

    public static string Resolve(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath) ?? throw new InvalidDataException("Update path has no root.");
        var current = root;
        foreach (var segment in fullPath[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (!info.Exists) continue;
            var target = info.ResolveLinkTarget(true);
            if (target is not null) current = target.FullName;
        }
        return Path.TrimEndingDirectorySeparator(current);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
