using Forge.Host.Domain;

internal static class AssetMaintenanceTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-maintenance-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var catalog = await AssetCatalogStore.OpenAsync(Path.Combine(root, "catalog"));
            var keepBytes = "portable moby"u8.ToArray();
            var keep = await catalog.PutAsync(AssetKind.Moby, 0, keepBytes, Metadata(1));
            var unused = await catalog.PutAsync(AssetKind.Tie, 0, "unused tie"u8.ToArray(), Metadata(2));
            var projects = Path.Combine(root, "projects");
            var projectPath = Path.Combine(projects, "map");
            var project = await ForgeProjectWorkspace.CreateAsync(
                projectPath,
                "Portable",
                new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
                new("UYA", "NTSC-U", "1.00", 3, UyaIsoService.SupportedMd5),
                [new(EntityId.New(), "Moby", "mobys", ProjectTransform.Identity, new(keep.Id, AssetKind.Moby),
                    new("UYA", 3, "gameplay/core/moby_instances", 1))]);

            var preview = await AssetCatalogMaintenance.PreviewAsync(catalog.RootPath, [projects]);
            Equal(1, preview.ProjectCount, "maintenance project count");
            Equal(keep.Id, catalog.Query(new(Id: keep.Id)).Single().Id, "referenced catalog entry");
            Equal(unused.Id, preview.Candidates.Single().Id, "maintenance candidate");
            await AssetCatalogMaintenance.CollectAsync(catalog.RootPath, [projects], preview.ConfirmationToken);
            catalog = await AssetCatalogStore.OpenAsync(catalog.RootPath);
            Equal(true, catalog.ResolveBlobPath(keep.Id) is not null, "maintenance preserves project reference");
            Equal(null, catalog.ResolveBlobPath(unused.Id), "maintenance removes unused asset");

            var matchingCatalog = await AssetCatalogStore.OpenAsync(Path.Combine(root, "matching-catalog"));
            var matching = await matchingCatalog.PutAsync(AssetKind.Moby, 0, keepBytes, Metadata(9));
            Equal(keep.Id, matching.Id, "matching catalog identity");
            var movedProjectPath = Path.Combine(root, "moved-project");
            Directory.Move(projectPath, movedProjectPath);
            project = await ForgeProjectWorkspace.OpenAsync(movedProjectPath);
            Equal(true, project.ResolveAssetPath(keep.Id, matchingCatalog) is not null,
                "moved project resolves against matching catalog");

            File.Delete(matchingCatalog.ResolveBlobPath(keep.Id)!);
            var descriptor = await UyaProjectService.InspectAsync(movedProjectPath, matchingCatalog);
            var missing = descriptor.MissingAssets.Single();
            Equal(keep.Id, missing.Id, "missing diagnostic ID");
            Equal(AssetKind.Moby, missing.Kind, "missing diagnostic kind");
            Equal(true, missing.Repairable, "missing diagnostic repair action");
            Equal(true, missing.Provenance.Any(value => value.Contains("level 3", StringComparison.Ordinal)),
                "missing diagnostic provenance");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static AssetImportMetadata Metadata(int sourceIndex) => new(
        "test-importer",
        new("UYA", "NTSC-U", "1.00", "level03", "level_wad/assets/asset_wad.bin", sourceIndex,
            UyaIsoService.SupportedMd5));

    private static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected {expected}, got {actual}");
    }
}
