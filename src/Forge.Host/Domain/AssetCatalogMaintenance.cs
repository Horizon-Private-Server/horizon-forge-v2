namespace Forge.Host.Domain;

public static class AssetCatalogMaintenance
{
    private static readonly EnumerationOptions ProjectSearchOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = false,
        AttributesToSkip = FileAttributes.ReparsePoint,
        MatchCasing = MatchCasing.PlatformDefault,
    };

    public static async Task<AssetCatalogMaintenanceReport> PreviewAsync(
        string catalogRootPath,
        IReadOnlyCollection<string> projectRoots,
        CancellationToken cancellationToken = default,
        Func<string, CancellationToken, Task<IReadOnlyList<AssetId>>>? readAdditionalReferences = null)
    {
        var references = await FindReferencesAsync(projectRoots, readAdditionalReferences, cancellationToken);
        var catalog = await AssetCatalogStore.OpenAsync(catalogRootPath, cancellationToken);
        var preview = catalog.PreviewGarbageCollection(references.AssetIds);
        return ToReport(preview, references);
    }

    public static async Task<AssetCatalogMaintenanceReport> CollectAsync(
        string catalogRootPath,
        IReadOnlyCollection<string> projectRoots,
        string confirmationToken,
        CancellationToken cancellationToken = default,
        Func<string, CancellationToken, Task<IReadOnlyList<AssetId>>>? readAdditionalReferences = null)
    {
        var references = await FindReferencesAsync(projectRoots, readAdditionalReferences, cancellationToken);
        if (references.Blockers.Count > 0)
            throw new InvalidDataException("Catalog cleanup is blocked because one or more known projects could not be inspected.");
        var catalog = await AssetCatalogStore.OpenAsync(catalogRootPath, cancellationToken);
        var collected = await catalog.CollectGarbageAsync(references.AssetIds, confirmationToken, cancellationToken);
        return ToReport(collected, references);
    }

    private static AssetCatalogMaintenanceReport ToReport(
        AssetGarbageCollectionPreview preview,
        ProjectReferences references)
    {
        var blockers = references.Blockers.Count <= 64
            ? references.Blockers
            : [.. references.Blockers.Take(63), $"… {references.Blockers.Count - 63} more blocked projects"];
        return new(
            references.ProjectCount,
            preview.CatalogAssetCount,
            preview.ProtectedAssetCount,
            preview.Candidates,
            preview.ConfirmationToken,
            blockers);
    }

    private static async Task<ProjectReferences> FindReferencesAsync(
        IReadOnlyCollection<string> roots,
        Func<string, CancellationToken, Task<IReadOnlyList<AssetId>>>? readAdditionalReferences,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roots);
        if (roots.Count > 1_024) throw new ArgumentException("Too many project roots were supplied.", nameof(roots));
        var projects = new HashSet<string>(PathComparer);
        var blockers = new List<string>();
        foreach (var value in roots.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string root;
            try { root = Path.GetFullPath(value); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                blockers.Add($"Invalid project root: {value}");
                continue;
            }
            if (!Directory.Exists(root))
            {
                blockers.Add($"Project root is unavailable: {root}");
                continue;
            }
            if (File.Exists(Path.Combine(root, ForgeProjectWorkspace.ManifestFileName)))
            {
                projects.Add(root);
                continue;
            }
            try
            {
                foreach (var manifest in Directory.EnumerateFiles(
                    root, ForgeProjectWorkspace.ManifestFileName, ProjectSearchOptions))
                    projects.Add(Path.GetDirectoryName(manifest)!);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                blockers.Add($"Could not scan project root {root}: {exception.Message}");
            }
        }

        var topLevelProjects = new List<string>();
        foreach (var project in projects.OrderBy(value => value.Length).ThenBy(value => value, PathComparer))
        {
            if (!topLevelProjects.Any(parent => IsInside(parent, project))) topLevelProjects.Add(project);
        }

        var ids = new HashSet<AssetId>();
        var inspected = 0;
        foreach (var projectPath in topLevelProjects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var workspace = await ForgeProjectWorkspace.OpenAsync(projectPath, cancellationToken);
                AddReferences(workspace, ids);
                foreach (var recovery in await workspace.ListRecoveriesAsync(cancellationToken))
                {
                    await workspace.LoadRecoveryAsync(recovery.Id, cancellationToken);
                    AddReferences(workspace, ids);
                }
                if (readAdditionalReferences is not null)
                    foreach (var id in await readAdditionalReferences(projectPath, cancellationToken)) ids.Add(id);
                inspected++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or InvalidDataException or System.Text.Json.JsonException)
            {
                blockers.Add($"Could not inspect project {projectPath}: {exception.Message}");
            }
        }
        return new(ids, inspected, blockers.Order(StringComparer.Ordinal).ToArray());
    }

    private static void AddReferences(ForgeProjectWorkspace workspace, HashSet<AssetId> ids)
    {
        foreach (var entity in workspace.Content.Entities)
            if (entity.Asset is not null) ids.Add(entity.Asset.Id);
        foreach (var asset in workspace.Content.Assets)
        {
            ids.Add(asset.Id);
            ids.Add(asset.ParentId);
        }
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static bool IsInside(string parent, string candidate) =>
        candidate.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private sealed record ProjectReferences(
        IReadOnlySet<AssetId> AssetIds,
        int ProjectCount,
        IReadOnlyList<string> Blockers);
}
