using Forge.Host.Domain;
using RatchetPs2.Core.Games;
using RatchetPs2.Games.UYA.Level;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.UYA;

public static class UyaProjectService
{
    private const string BaseLayerImporterVersion = "forge-uya-base-v2";

    public static async Task<UyaProjectCreationOptions> GetCreationOptionsAsync(
        string sourceIsoPath,
        CancellationToken cancellationToken = default)
    {
        await using var iso = OpenIso(sourceIsoPath);
        cancellationToken.ThrowIfCancellationRequested();
        return GetCreationOptions(iso);
    }

    public static UyaProjectCreationOptions GetCreationOptions(Stream iso) =>
        new(LevelCatalogReader.FindAvailable(GameId.UYA, iso), []);

    public static async Task<UyaProjectPreflight> PreflightAsync(
        string sourceIsoPath,
        string catalogRootPath,
        int level,
        CancellationToken cancellationToken = default)
    {
        await using var iso = OpenIso(sourceIsoPath);
        var catalog = await AssetCatalogStore.OpenReadOnlyAsync(catalogRootPath, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return Preflight(iso, catalog, level, cancellationToken);
    }

    public static UyaProjectPreflight Preflight(
        Stream iso, AssetCatalogStore catalog, int level, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(iso);
        ArgumentNullException.ThrowIfNull(catalog);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateLevel(iso, level);
        return UyaBaseLevelService.Preflight(iso, catalog, level, cancellationToken);
    }

    public static async Task<ForgeProjectDescriptor> CreateAsync(
        UyaProjectCreationRequest request,
        Func<IsoProgress, ValueTask>? progress = null,
        CancellationToken cancellationToken = default,
        MobyDexCatalog? mobyDex = null)
    {
        ValidateRequest(request);
        ValidateDestination(request);
        await using var iso = OpenIso(request.SourceIsoPath, FileOptions.SequentialScan);
        using var preparationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var verification = UyaIsoService.ValidateAsync(
            iso,
            progress is null ? null : value => ReportScaledAsync(progress, value, 0, 3_000),
            preparationCancellation.Token);
        var preparation = Task.Run(async () =>
        {
            var snapshot = await AssetCatalogStore.OpenReadOnlyAsync(
                request.CatalogRootPath, preparationCancellation.Token);
            var data = ReadBase(iso, snapshot, request.Level, mobyDex, preparationCancellation.Token);
            return (snapshot.Revision, Data: data);
        }, preparationCancellation.Token);
        try
        {
            var identity = await verification;
            if (!identity.IsSupported || !identity.Fingerprint.Equals(request.Fingerprint, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The UYA source ISO no longer matches its verified fingerprint.");
            var prepared = await preparation;
            var catalog = await AssetCatalogStore.OpenAsync(request.CatalogRootPath, cancellationToken);
            var baseData = prepared.Data;
            if (catalog.Revision != prepared.Revision)
            {
                baseData = ReadBase(iso, catalog, request.Level, mobyDex, cancellationToken);
            }
            Func<IsoProgress, ValueTask>? creationProgress = progress is null ? null
                : value => ReportScaledAsync(progress, value, 3_000, 10_000);
            await ReportAsync(creationProgress, 1, 4);
            cancellationToken.ThrowIfCancellationRequested();
            return await CreatePreparedAsync(catalog, request, baseData, creationProgress, cancellationToken);
        }
        catch
        {
            preparationCancellation.Cancel();
            // Both readers must stop before their shared source handle is disposed.
            try { await Task.WhenAll(verification, preparation); }
            catch { }
            throw;
        }
    }

    public static async Task<ForgeProjectDescriptor> CreateValidatedAsync(
        Stream iso,
        AssetCatalogStore catalog,
        UyaProjectCreationRequest request,
        Func<IsoProgress, ValueTask>? progress = null,
        CancellationToken cancellationToken = default,
        MobyDexCatalog? mobyDex = null)
    {
        ValidateRequest(request);
        ArgumentNullException.ThrowIfNull(iso);
        ArgumentNullException.ThrowIfNull(catalog);
        if (!iso.CanRead || !iso.CanSeek) throw new ArgumentException("The UYA ISO stream must be readable and seekable.", nameof(iso));
        ValidateDestination(request);

        await ReportAsync(progress, 1, 4);
        cancellationToken.ThrowIfCancellationRequested();
        var baseData = ReadBase(iso, catalog, request.Level, mobyDex, cancellationToken);
        return await CreatePreparedAsync(catalog, request, baseData, progress, cancellationToken);
    }

    private static async Task<ForgeProjectDescriptor> CreatePreparedAsync(
        AssetCatalogStore catalog,
        UyaProjectCreationRequest request,
        UyaBaseLevelData baseData,
        Func<IsoProgress, ValueTask>? progress,
        CancellationToken cancellationToken)
    {
        ValidateDestination(request);
        var warnings = UyaBaseLevelService.CreateWarnings(
            baseData.MissingInstanceCount, baseData.MissingClassCount, baseData.FallbackTransformCount);
        if (!request.AllowPartial && warnings.Count > 0) throw new InvalidDataException(string.Join(' ', warnings));
        await ReportAsync(progress, 2, 4);
        var entities = baseData.Entities;
        if (entities.Count + baseData.Tfrags.Entities.Count == 0)
            throw new InvalidDataException($"UYA level {request.Level} does not contain any supported base instances.");
        await ReportAsync(progress, 3, 4);

        var workspace = await ForgeProjectWorkspace.CreateAsync(
            request.ProjectPath,
            request.Name,
            new("UYA", "NTSC-U", request.Revision, "uya-ntsc-u"),
            new("UYA", "NTSC-U", request.Revision, request.Level, request.Fingerprint.ToLowerInvariant(),
                baseData.MissingInstanceCount),
            entities,
            baseData.LevelSettings,
            baseData.Hud,
            baseData.Fx,
            baseData.MobyPVars,
            cancellationToken);
        await workspace.SetSoundsAsync(baseData.Sounds, cancellationToken: cancellationToken);
        await UyaTfragProjectService.ApplyAsync(workspace, baseData.Tfrags, cancellationToken);
        await workspace.SaveAsync(cancellationToken);
        await OpaqueContentStore.WriteAsync(
            workspace.RootPath,
            OpaqueSource(request.Revision, request.Level, request.Fingerprint),
            baseData.OpaqueSections,
            cancellationToken);
        await WriteBaseLayersAsync(
            workspace.RootPath,
            catalog,
            OpaqueSource(request.Revision, request.Level, request.Fingerprint),
            baseData.BaseLayers,
            cancellationToken);
        await UyaStaticLayerStore.WriteSourceAsync(
            workspace.RootPath,
            OpaqueSource(request.Revision, request.Level, request.Fingerprint),
            baseData.StaticLayers,
            cancellationToken);
        await UyaGameplayLayerStore.WriteSourceAsync(
            workspace.RootPath,
            OpaqueSource(request.Revision, request.Level, request.Fingerprint),
            baseData.Gameplay,
            cancellationToken);
        if (request.ApplyGroupTemplates
            && await UyaGroupTemplateService.ApplyDefaultsAsync(workspace, cancellationToken) > 0)
            await workspace.SaveAsync(cancellationToken);
        await ReportAsync(progress, 4, 4);
        return await InspectAsync(workspace.RootPath, catalog, warnings, cancellationToken);
    }

    public static async Task<ForgeProjectDescriptor> InspectAsync(
        string projectPath,
        AssetCatalogStore catalog,
        IReadOnlyList<string>? warnings = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var workspace = await ForgeProjectWorkspace.OpenAsync(projectPath, cancellationToken);
        var opaque = await OpaqueContentStore.InspectAsync(projectPath, cancellationToken);
        var baseLayers = await UyaBaseLayerStore.InspectAsync(projectPath, catalog, cancellationToken);
        var staticLayers = await UyaStaticLayerStore.InspectAsync(projectPath, cancellationToken);
        var gameplay = await UyaGameplayLayerStore.InspectAsync(projectPath, cancellationToken);
        var missingAssets = FindMissingAssets(workspace, catalog);
        var missing = workspace.Manifest.BaseLevel.MissingAssetCount + missingAssets.Sum(asset => asset.EntityCount);
        var manifestPath = Path.Combine(workspace.RootPath, ForgeProjectWorkspace.ManifestFileName);
        var contentPath = workspace.ContentFilePath;
        var modified = new[] { File.GetLastWriteTimeUtc(manifestPath), File.GetLastWriteTimeUtc(contentPath) }.Max();
        var modifiedUnixMilliseconds = new DateTimeOffset(modified).ToUnixTimeMilliseconds();
        var recoveries = (await workspace.ListRecoveriesAsync(cancellationToken))
            .Where(recovery => recovery.CreatedUnixMilliseconds > modifiedUnixMilliseconds)
            .ToArray();
        return new(
            workspace.RootPath,
            workspace.Manifest.Name,
            workspace.Manifest.Target.Game,
            workspace.Manifest.Target.Region,
            workspace.Manifest.Target.Revision,
            workspace.Manifest.Target.BakeProfile,
            workspace.Manifest.BaseLevel.Level,
            modifiedUnixMilliseconds,
            workspace.Content.Entities.Count,
            missing,
            workspace.IsDirty,
            !opaque.IsValid || !baseLayers.IsValid || !staticLayers.IsValid || !gameplay.IsValid,
            (warnings ?? []).Concat(opaque.Blockers)
                .Concat(baseLayers.Blockers.Values.SelectMany(value => value))
                .Concat(staticLayers.Blockers.Values.SelectMany(value => value))
                .Concat(gameplay.Blockers)
                .Distinct(StringComparer.Ordinal).ToArray(),
            recoveries,
            missingAssets);
    }

    public static async Task<ForgeProjectDescriptor> RepairMissingAssetsAsync(
        string projectPath,
        string catalogRootPath,
        string sourceIsoPath,
        string importerVersion,
        Func<IsoProgress, ValueTask>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var workspace = await ForgeProjectWorkspace.OpenAsync(projectPath, cancellationToken);
        var catalog = await AssetCatalogStore.OpenAsync(catalogRootPath, cancellationToken);
        var missing = FindMissingAssets(workspace, catalog).Where(asset => asset.Repairable).Select(asset => asset.Id).ToHashSet();
        var levels = workspace.Content.Entities
            .Where(entity => entity.Asset is not null && missing.Contains(entity.Asset.Id))
            .Select(entity => entity.Provenance?.Level)
            .OfType<int>()
            .Distinct()
            .Order()
            .ToArray();
        if (levels.Length == 0) return await InspectAsync(projectPath, catalog, cancellationToken: cancellationToken);

        await UyaAssetImportService.RepairLevelsAsync(
            new(sourceIsoPath, catalogRootPath, workspace.Manifest.BaseLevel.SourceFingerprint,
                workspace.Manifest.BaseLevel.Revision, importerVersion),
            levels,
            progress,
            cancellationToken);
        catalog = await AssetCatalogStore.OpenAsync(catalogRootPath, cancellationToken);
        return await InspectAsync(projectPath, catalog, cancellationToken: cancellationToken);
    }

    public static async Task<ForgeProjectDescriptor> RenameAsync(
        string projectPath,
        string name,
        AssetCatalogStore catalog,
        CancellationToken cancellationToken = default)
    {
        var workspace = await ForgeProjectWorkspace.OpenAsync(projectPath, cancellationToken);
        workspace.Rename(name);
        await workspace.SaveAsync(cancellationToken);
        return await InspectAsync(projectPath, catalog, cancellationToken: cancellationToken);
    }

    public static async Task<ForgeProjectDescriptor> RestoreRecoveryAsync(
        string projectPath,
        string recoveryId,
        AssetCatalogStore catalog,
        CancellationToken cancellationToken = default)
    {
        var workspace = await ForgeProjectWorkspace.OpenAsync(projectPath, cancellationToken);
        await workspace.LoadRecoveryAsync(recoveryId, cancellationToken);
        await workspace.SaveAsync(cancellationToken);
        return await InspectAsync(projectPath, catalog, cancellationToken: cancellationToken);
    }

    public static async Task<ForgeProjectDescriptor> RepairAsync(
        string projectPath,
        AssetCatalogStore catalog,
        string sourceIsoPath,
        CancellationToken cancellationToken = default)
    {
        var workspace = await ForgeProjectWorkspace.OpenAsync(projectPath, cancellationToken);
        var opaque = await OpaqueContentStore.InspectAsync(projectPath, cancellationToken);
        var baseLayers = await UyaBaseLayerStore.InspectAsync(projectPath, catalog, cancellationToken);
        var staticLayers = await UyaStaticLayerStore.InspectAsync(projectPath, cancellationToken);
        var gameplay = await UyaGameplayLayerStore.InspectAsync(projectPath, cancellationToken);
        if (opaque.IsValid
            && baseLayers.IsValid
            && staticLayers.IsValid
            && gameplay.IsValid
            && workspace.Content.Sounds.Programs.Count > 0)
            return await SaveRepairAsync(workspace, catalog, cancellationToken);
        var identity = await UyaIsoService.ValidateAsync(sourceIsoPath, cancellationToken: cancellationToken);
        if (!identity.IsSupported || !identity.Fingerprint.Equals(
                workspace.Manifest.BaseLevel.SourceFingerprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The UYA source ISO does not match this project's verified source fingerprint.");
        await using var iso = OpenIso(sourceIsoPath);
        return await RepairValidatedAsync(projectPath, catalog, iso, cancellationToken);
    }

    public static async Task<ForgeProjectDescriptor> RepairValidatedAsync(
        string projectPath,
        AssetCatalogStore catalog,
        Stream iso,
        CancellationToken cancellationToken = default)
    {
        var workspace = await ForgeProjectWorkspace.OpenAsync(projectPath, cancellationToken);
        var opaque = await OpaqueContentStore.InspectAsync(projectPath, cancellationToken);
        var baseLayers = await UyaBaseLayerStore.InspectAsync(projectPath, catalog, cancellationToken);
        var staticLayers = await UyaStaticLayerStore.InspectAsync(projectPath, cancellationToken);
        var gameplay = await UyaGameplayLayerStore.InspectAsync(projectPath, cancellationToken);
        if (!opaque.IsValid
            || !baseLayers.IsValid
            || !staticLayers.IsValid
            || !gameplay.IsValid
            || workspace.Content.Sounds.Programs.Count == 0)
        {
            var baseData = ReadBase(iso, catalog, workspace.Manifest.BaseLevel.Level, cancellationToken: cancellationToken);
            if (workspace.Content.Sounds.Programs.Count == 0)
                await workspace.SetSoundsAsync(baseData.Sounds, cancellationToken: cancellationToken);
            await OpaqueContentStore.WriteAsync(
                workspace.RootPath,
                OpaqueSource(
                    workspace.Manifest.BaseLevel.Revision,
                    workspace.Manifest.BaseLevel.Level,
                    workspace.Manifest.BaseLevel.SourceFingerprint),
                baseData.OpaqueSections,
                cancellationToken);
            await WriteBaseLayersAsync(
                workspace.RootPath,
                catalog,
                OpaqueSource(
                    workspace.Manifest.BaseLevel.Revision,
                    workspace.Manifest.BaseLevel.Level,
                    workspace.Manifest.BaseLevel.SourceFingerprint),
                baseData.BaseLayers,
                cancellationToken);
            await UyaStaticLayerStore.WriteSourceAsync(
                workspace.RootPath,
                OpaqueSource(
                    workspace.Manifest.BaseLevel.Revision,
                    workspace.Manifest.BaseLevel.Level,
                    workspace.Manifest.BaseLevel.SourceFingerprint),
                baseData.StaticLayers,
                cancellationToken);
            await UyaGameplayLayerStore.WriteSourceAsync(
                workspace.RootPath,
                OpaqueSource(
                    workspace.Manifest.BaseLevel.Revision,
                    workspace.Manifest.BaseLevel.Level,
                    workspace.Manifest.BaseLevel.SourceFingerprint),
                baseData.Gameplay,
                cancellationToken);
        }
        return await SaveRepairAsync(workspace, catalog, cancellationToken);
    }

    private static async Task<ForgeProjectDescriptor> SaveRepairAsync(
        ForgeProjectWorkspace workspace,
        AssetCatalogStore catalog,
        CancellationToken cancellationToken)
    {
        if (workspace.IsDirty) await workspace.SaveAsync(cancellationToken);
        return await InspectAsync(workspace.RootPath, catalog, cancellationToken: cancellationToken);
    }

    private static IReadOnlyList<MissingProjectAsset> FindMissingAssets(
        ForgeProjectWorkspace workspace,
        AssetCatalogStore catalog)
    {
        var attached = workspace.Content.Assets.Select(asset => asset.Id).ToHashSet();
        return workspace.Content.Entities
            .Where(entity => entity.Asset is not null && workspace.ResolveAsset(entity.Asset, catalog) is null)
            .GroupBy(entity => entity.Asset!)
            .OrderBy(group => group.Key.Id.ToString(), StringComparer.Ordinal)
            .Select(group =>
            {
                var catalogEntry = catalog.Query(new(Id: group.Key.Id)).SingleOrDefault();
                var provenance = group.Select(entity => entity.Provenance is null
                        ? "Project entity with no source provenance"
                        : $"{entity.Provenance.Game} level {entity.Provenance.Level}, {entity.Provenance.Section} #{entity.Provenance.SourceIndex}")
                    .Concat(catalogEntry?.Sources.Select(source =>
                        $"{source.Game} {source.Region} {source.Revision}, {source.Level}, {source.Archive} #{source.SourceIndex}") ?? [])
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                if (provenance.Length > 64)
                    provenance = [.. provenance.Take(63), $"… {provenance.Length - 63} more provenance records"];
                var repairable = !attached.Contains(group.Key.Id)
                    && group.Any(entity => entity.Provenance?.Game == "UYA");
                return new MissingProjectAsset(group.Key.Id, group.Key.Kind, group.Count(), repairable, provenance);
            })
            .ToArray();
    }

    private static UyaBaseLevelData ReadBase(
        Stream iso,
        AssetCatalogStore catalog,
        int level,
        MobyDexCatalog? mobyDex = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateLevel(iso, level);
        return UyaBaseLevelService.Read(iso, catalog, level, mobyDex, cancellationToken);
    }

    private static void ValidateLevel(Stream iso, int level)
    {
        if (!iso.CanRead || !iso.CanSeek) throw new ArgumentException("The UYA ISO stream must be readable and seekable.", nameof(iso));
        if (!LevelCatalogReader.FindAvailable(GameId.UYA, iso).Contains(level))
            throw new InvalidDataException($"UYA level {level} is not present in the source ISO.");
    }

    private static OpaqueContentSource OpaqueSource(string revision, int level, string fingerprint) =>
        new("UYA", "NTSC-U", revision, level, fingerprint);

    private static async Task WriteBaseLayersAsync(
        string projectRoot,
        AssetCatalogStore catalog,
        OpaqueContentSource source,
        IReadOnlyList<UyaBaseLayerPayload> payloads,
        CancellationToken cancellationToken)
    {
        var entries = await catalog.PutManyAsync(
            UyaBaseLayerService.CreateCatalogPuts(payloads, source, BaseLayerImporterVersion),
            cancellationToken);
        await UyaBaseLayerStore.WriteAsync(projectRoot, source, payloads, entries, cancellationToken);
    }

    private static FileStream OpenIso(string path, FileOptions accessPattern = FileOptions.RandomAccess)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | accessPattern);
    }

    private static void ValidateDestination(UyaProjectCreationRequest request)
    {
        if (Directory.Exists(request.ProjectPath) && Directory.EnumerateFileSystemEntries(request.ProjectPath).Any())
            throw new IOException($"The project directory is not empty: {request.ProjectPath}");
    }

    private static void ValidateRequest(UyaProjectCreationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceIsoPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CatalogRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProjectPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Revision);
        if (request.Name.Length > 256) throw new ArgumentException("Project name cannot exceed 256 characters.", nameof(request));
        if (request.Fingerprint.Length != 32 || !request.Fingerprint.All(Uri.IsHexDigit))
            throw new ArgumentException("Project source fingerprint must be a 32-character MD5 value.", nameof(request));
        if (request.Level < 0 || request.Level >= UyaLevelConstants.LevelInfoCount)
            throw new ArgumentOutOfRangeException(nameof(request), "UYA base level is out of range.");
    }

    private static ValueTask ReportAsync(Func<IsoProgress, ValueTask>? progress, long completed, long total) =>
        progress?.Invoke(new(completed, total)) ?? ValueTask.CompletedTask;

    private static ValueTask ReportScaledAsync(Func<IsoProgress, ValueTask> progress, IsoProgress value, long start, long end)
    {
        var completed = value.Total <= 0 ? start : start + value.Completed * (end - start) / value.Total;
        return progress(new(completed, 10_000));
    }
}
