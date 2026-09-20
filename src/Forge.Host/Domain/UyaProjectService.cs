using RatchetPs2.Games.UYA.Gameplay;
using RatchetPs2.Games.UYA.Level;
using RatchetPs2.Games.DL.Level;

namespace Forge.Host.Domain;

public static class UyaProjectService
{
    private const string PartialBaseWarning =
        "The pinned SDK currently exposes UYA moby instances only. Tie, shrub, and other base content will remain sourced from the base WAD but is not yet editable as scene entities.";

    public static async Task<UyaProjectCreationOptions> GetCreationOptionsAsync(
        string sourceIsoPath,
        CancellationToken cancellationToken = default)
    {
        await using var iso = OpenIso(sourceIsoPath);
        cancellationToken.ThrowIfCancellationRequested();
        return GetCreationOptions(iso);
    }

    public static UyaProjectCreationOptions GetCreationOptions(Stream iso) =>
        new(FindLevels(iso), [PartialBaseWarning]);

    public static async Task<UyaProjectPreflight> PreflightAsync(
        string sourceIsoPath,
        string catalogRootPath,
        int level,
        CancellationToken cancellationToken = default)
    {
        await using var iso = OpenIso(sourceIsoPath);
        var catalog = await AssetCatalogStore.OpenAsync(catalogRootPath, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return Preflight(iso, catalog, level);
    }

    public static UyaProjectPreflight Preflight(Stream iso, AssetCatalogStore catalog, int level)
    {
        ArgumentNullException.ThrowIfNull(iso);
        ArgumentNullException.ThrowIfNull(catalog);
        var baseData = ReadBase(iso, catalog, level);
        return new(
            level,
            baseData.Instances.Count,
            baseData.Instances.Count(instance => baseData.Assets.ContainsKey(instance.ClassId)),
            baseData.ModelLessInstanceCount,
            baseData.MissingInstanceCount,
            baseData.MissingClasses.Count,
            CreateWarnings(baseData));
    }

    public static async Task<ForgeProjectDescriptor> CreateAsync(
        UyaProjectCreationRequest request,
        Func<IsoProgress, ValueTask>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);

        var identity = await UyaIsoService.ValidateAsync(
            request.SourceIsoPath,
            progress is null ? null : value => ReportScaledAsync(progress, value, 0, 3_000),
            cancellationToken);
        if (!identity.IsSupported || !identity.Fingerprint.Equals(request.Fingerprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The UYA source ISO no longer matches its verified fingerprint.");

        await using var iso = OpenIso(request.SourceIsoPath);
        var catalog = await AssetCatalogStore.OpenAsync(request.CatalogRootPath, cancellationToken);
        return await CreateValidatedAsync(
            iso,
            catalog,
            request,
            progress is null ? null : value => ReportScaledAsync(progress, value, 3_000, 10_000),
            cancellationToken);
    }

    public static async Task<ForgeProjectDescriptor> CreateValidatedAsync(
        Stream iso,
        AssetCatalogStore catalog,
        UyaProjectCreationRequest request,
        Func<IsoProgress, ValueTask>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        ArgumentNullException.ThrowIfNull(iso);
        ArgumentNullException.ThrowIfNull(catalog);
        if (!iso.CanRead || !iso.CanSeek) throw new ArgumentException("The UYA ISO stream must be readable and seekable.", nameof(iso));
        if (Directory.Exists(request.ProjectPath) && Directory.EnumerateFileSystemEntries(request.ProjectPath).Any())
            throw new IOException($"The project directory is not empty: {request.ProjectPath}");

        await ReportAsync(progress, 1, 4);
        cancellationToken.ThrowIfCancellationRequested();
        var baseData = ReadBase(iso, catalog, request.Level);
        var warnings = CreateWarnings(baseData);
        if (!request.AllowPartial && warnings.Count > 0) throw new InvalidDataException(string.Join(' ', warnings));
        await ReportAsync(progress, 2, 4);
        var entities = baseData.Instances.Select((instance, index) => CreateEntity(
            instance, index, request.Level, baseData.Assets, baseData.ModelClassIds)).Where(entity => entity is not null).Cast<ProjectEntity>().ToArray();
        if (entities.Length == 0) throw new InvalidDataException($"UYA level {request.Level} does not contain any usable moby instances.");
        await ReportAsync(progress, 3, 4);

        var workspace = await ForgeProjectWorkspace.CreateAsync(
            request.ProjectPath,
            request.Name,
            new("UYA", "NTSC-U", request.Revision, "uya-ntsc-u"),
            new("UYA", "NTSC-U", request.Revision, request.Level, request.Fingerprint.ToLowerInvariant(), baseData.MissingInstanceCount),
            entities,
            cancellationToken);
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
        var missing = workspace.Manifest.BaseLevel.MissingAssetCount + workspace.Content.Entities.Count(entity => entity.Asset is not null
            && workspace.ResolveAssetPath(entity.Asset.Id, catalog) is null);
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
            workspace.MigrationPending,
            warnings ?? [],
            recoveries);
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

    public static async Task<ForgeProjectDescriptor> MigrateAsync(
        string projectPath,
        AssetCatalogStore catalog,
        CancellationToken cancellationToken = default)
    {
        var workspace = await ForgeProjectWorkspace.OpenAsync(projectPath, cancellationToken);
        if (workspace.MigrationPending) await workspace.SaveAsync(cancellationToken);
        return await InspectAsync(projectPath, catalog, cancellationToken: cancellationToken);
    }

    private static ProjectEntity? CreateEntity(
        UyaMobyInstance instance,
        int index,
        int level,
        IReadOnlyDictionary<int, AssetCatalogEntry> assets,
        IReadOnlySet<int> modelClassIds)
    {
        assets.TryGetValue(instance.ClassId, out var asset);
        if (asset is null && modelClassIds.Contains(instance.ClassId)) return null;
        return new(
            EntityId.New(),
            $"Moby 0x{instance.ClassId:X4} #{index}",
            "mobys",
            new(
                new(instance.Position.X, instance.Position.Y, instance.Position.Z),
                FromZyxEuler(instance.Rotation),
                new(instance.Scale, instance.Scale, instance.Scale)),
            asset is null ? null : new(asset.Id, AssetKind.Moby),
            new("UYA", level, "gameplay/core/moby_instances", index));
    }

    private static ProjectQuaternion FromZyxEuler(UyaVector3 rotation)
    {
        var c1 = MathF.Cos(rotation.X / 2);
        var c2 = MathF.Cos(rotation.Y / 2);
        var c3 = MathF.Cos(rotation.Z / 2);
        var s1 = MathF.Sin(rotation.X / 2);
        var s2 = MathF.Sin(rotation.Y / 2);
        var s3 = MathF.Sin(rotation.Z / 2);
        return new(
            s1 * c2 * c3 - c1 * s2 * s3,
            c1 * s2 * c3 + s1 * c2 * s3,
            c1 * c2 * s3 - s1 * s2 * c3,
            c1 * c2 * c3 + s1 * s2 * s3);
    }

    private static BaseData ReadBase(Stream iso, AssetCatalogStore catalog, int level)
    {
        if (!iso.CanRead || !iso.CanSeek) throw new ArgumentException("The UYA ISO stream must be readable and seekable.", nameof(iso));
        if (!FindLevels(iso).Contains(level)) throw new InvalidDataException($"UYA level {level} is not present in the source ISO.");
        var levelWad = UyaLooseLevelWadExtractor.ExtractPrimary(iso, level).Bytes;
        var package = UyaLevelWadUnpacker.Unpack(levelWad);
        var mobyBytes = package.Files.SingleOrDefault(file => file.Path == "gameplay/core/moby_instances.bin")?.Bytes
            ?? throw new InvalidDataException($"UYA level {level} does not contain readable moby instances.");
        var instances = UyaMobyInstancesReader.Read(mobyBytes).Instances;
        var source = UyaLevelWadRenderPackageBuilder.ReadAssetSourceFiles(package.Files);
        var header = DlAssetReader.ReadHeader(source.HeaderBytes);
        var modelClassIds = DlAssetReader.ReadModelDefinitions(
            source.HeaderBytes, header.MobyModelOffset, header.MobyModelCount)
            .Where(model => model.ModelOffset > 0)
            .Select(model => model.ModelId)
            .ToHashSet();
        var assets = BuildMobyAssetLookup(catalog, level);
        var missingClasses = instances.Select(instance => instance.ClassId)
            .Where(classId => modelClassIds.Contains(classId) && !assets.ContainsKey(classId)).Distinct().Order().ToArray();
        return new(
            instances,
            assets,
            modelClassIds,
            missingClasses,
            instances.Count(instance => modelClassIds.Contains(instance.ClassId) && !assets.ContainsKey(instance.ClassId)),
            instances.Count(instance => !modelClassIds.Contains(instance.ClassId)));
    }

    private static IReadOnlyList<string> CreateWarnings(BaseData value)
    {
        var warnings = new List<string> { PartialBaseWarning };
        if (value.MissingInstanceCount > 0)
            warnings.Add($"{value.MissingInstanceCount} moby instances across {value.MissingClasses.Count} class IDs are absent from the global catalog and will be skipped.");
        return warnings;
    }

    private static IReadOnlyDictionary<int, AssetCatalogEntry> BuildMobyAssetLookup(AssetCatalogStore catalog, int level)
    {
        var result = new Dictionary<int, AssetCatalogEntry>();
        foreach (var asset in catalog.Query(new(Kind: AssetKind.Moby, Game: "UYA", Level: $"level{level:00}", Limit: AssetCatalogStore.MaxQueryLimit)))
        {
            var alias = asset.Aliases.FirstOrDefault(value => value.StartsWith("moby:", StringComparison.Ordinal) && !value.StartsWith("moby:0x", StringComparison.Ordinal));
            if (alias is not null && int.TryParse(alias.AsSpan(5), out var classId)) result.TryAdd(classId, asset);
        }
        return result;
    }

    private static IReadOnlyList<int> FindLevels(Stream iso)
    {
        var levels = new List<int>();
        for (var level = 0; level < UyaLevelConstants.LevelInfoCount; level++)
            if (!UyaLevelInfoReader.ReadEntry(iso, level).LevelWad.IsEmpty) levels.Add(level);
        return levels;
    }

    private static FileStream OpenIso(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
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

    private sealed record BaseData(
        IReadOnlyList<UyaMobyInstance> Instances,
        IReadOnlyDictionary<int, AssetCatalogEntry> Assets,
        IReadOnlySet<int> ModelClassIds,
        IReadOnlyList<int> MissingClasses,
        int MissingInstanceCount,
        int ModelLessInstanceCount);
}
