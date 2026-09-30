namespace Forge.Host.Domain;

public sealed class ForgeProjectWorkspace
{
    public const string ManifestFileName = "forge-project.json";
    public const string DefaultContentPath = "content/project.json";
    public const string RecoveryDirectoryName = ForgeProjectPersistence.RecoveryDirectoryName;
    public const int MaxRecoverySnapshots = ForgeProjectPersistence.MaxRecoverySnapshots;
    public const long MaxRecoveryBytes = ForgeProjectPersistence.MaxRecoveryBytes;
    private string _savedFingerprint;
    private bool _migrationPending;

    private ForgeProjectWorkspace(
        string rootPath,
        ForgeProjectManifest manifest,
        ForgeProjectContent content,
        bool migrationPending = false)
    {
        RootPath = rootPath;
        Manifest = manifest;
        Content = content;
        _savedFingerprint = ForgeProjectPersistence.Fingerprint(manifest, content);
        _migrationPending = migrationPending;
    }

    public string RootPath { get; }
    public ForgeProjectManifest Manifest { get; private set; }
    public ForgeProjectContent Content { get; private set; }
    public string ContentFilePath => ForgeProjectPersistence.ResolveRelativePath(RootPath, Manifest.Content);
    public string CurrentFingerprint => ForgeProjectPersistence.Fingerprint(Manifest, Content);
    public bool IsDirty => _migrationPending || CurrentFingerprint != _savedFingerprint;
    public bool MigrationPending => _migrationPending
        || Manifest.BaseLevel.EntityVersion < ProjectSchema.CurrentBaseEntityVersion;

    internal ForgeProjectState CaptureState() => new(Manifest, Content);

    internal void RestoreState(ForgeProjectState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var previousManifest = Manifest;
        var previousContent = Content;
        try
        {
            Manifest = state.Manifest;
            Content = state.Content;
            Validate();
        }
        catch
        {
            Manifest = previousManifest;
            Content = previousContent;
            throw;
        }
    }

    public static async Task<ForgeProjectWorkspace> CreateAsync(
        string rootPath,
        string name,
        ProjectTargetProfile target,
        ProjectBaseLevel baseLevel,
        IReadOnlyList<ProjectEntity> entities,
        CancellationToken cancellationToken = default) => await CreateAsync(
            rootPath, name, target, baseLevel, entities, null, cancellationToken);

    public static async Task<ForgeProjectWorkspace> CreateAsync(
        string rootPath,
        string name,
        ProjectTargetProfile target,
        ProjectBaseLevel baseLevel,
        IReadOnlyList<ProjectEntity> entities,
        ProjectLevelSettings? levelSettings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(baseLevel);
        ArgumentNullException.ThrowIfNull(entities);
        var root = Path.GetFullPath(rootPath);
        var manifestPath = Path.Combine(root, ManifestFileName);
        if (File.Exists(manifestPath)) throw new IOException($"A Forge project already exists at {root}.");
        var workspace = new ForgeProjectWorkspace(
            root,
            new(ProjectSchema.CurrentVersion, ProjectSchema.ManifestDocumentType, EntityId.New(), name, target, baseLevel, DefaultContentPath),
            new(ProjectSchema.CurrentVersion, ProjectSchema.ContentDocumentType, entities.ToArray(), [], levelSettings));
        workspace.Validate();
        await workspace.SaveAsync(cancellationToken);
        return workspace;
    }

    public static async Task<ForgeProjectWorkspace> OpenAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(rootPath);
        var loaded = await ForgeProjectPersistence.LoadAsync(root, cancellationToken);
        var workspace = new ForgeProjectWorkspace(root, loaded.Manifest, loaded.Content, loaded.Migrated);
        workspace.Validate();
        return workspace;
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        Validate();
        await ForgeProjectPersistence.SaveAsync(RootPath, Manifest, Content, cancellationToken);
        _savedFingerprint = CurrentFingerprint;
        _migrationPending = false;
    }

    public async Task<ProjectRecoverySnapshot?> WriteRecoveryAsync(CancellationToken cancellationToken = default)
    {
        Validate();
        return IsDirty
            ? await ForgeProjectPersistence.WriteRecoveryAsync(RootPath, Manifest, Content, cancellationToken)
            : null;
    }

    public Task<IReadOnlyList<ProjectRecoverySnapshot>> ListRecoveriesAsync(CancellationToken cancellationToken = default) =>
        ForgeProjectPersistence.ListRecoveriesAsync(RootPath, cancellationToken);

    public async Task LoadRecoveryAsync(string recoveryId, CancellationToken cancellationToken = default)
    {
        var loaded = await ForgeProjectPersistence.LoadRecoveryAsync(RootPath, recoveryId, cancellationToken);
        if (loaded.Manifest.ProjectId != Manifest.ProjectId)
            throw new InvalidDataException("Recovery belongs to a different project.");
        var previousManifest = Manifest;
        var previousContent = Content;
        var previousMigration = _migrationPending;
        try
        {
            Manifest = loaded.Manifest;
            Content = loaded.Content;
            _migrationPending = loaded.Migrated;
            Validate();
        }
        catch
        {
            Manifest = previousManifest;
            Content = previousContent;
            _migrationPending = previousMigration;
            throw;
        }
    }

    public void UpdateTransform(EntityId entityId, ProjectTransform transform)
    {
        ForgeProjectValidation.ValidateTransform(transform);
        var index = FindEntityIndex(entityId);
        var entities = Content.Entities.ToArray();
        entities[index] = entities[index] with { Transform = transform };
        Content = Content with { Entities = entities };
    }

    public void UpdateTransforms(IReadOnlyList<EditorTransformUpdate> updates)
    {
        ArgumentNullException.ThrowIfNull(updates);
        foreach (var update in updates)
        {
            ForgeProjectValidation.ValidateTransform(update.Transform);
            FindEntityIndex(update.EntityId);
        }
        var transforms = updates.ToDictionary(update => update.EntityId, update => update.Transform);
        Content = Content with
        {
            Entities = Content.Entities.Select(entity => transforms.TryGetValue(entity.EntityId, out var transform)
                ? entity with { Transform = transform }
                : entity).ToArray(),
        };
    }

    public void UpdateLevelSettings(ProjectLevelSettings settings)
    {
        ForgeProjectValidation.ValidateLevelSettings(settings);
        Content = Content with { LevelSettings = settings };
    }

    public void UpdateSplinePoints(EntityId entityId, IReadOnlyList<ProjectVector4> points)
    {
        if (!ForgeProjectValidation.ValidPoints(points)) throw new InvalidDataException("Spline points must be finite.");
        var index = FindEntityIndex(entityId);
        var entity = Content.Entities[index];
        if (entity.Geometry?.Spline is null && entity.Geometry?.GrindPath is null)
            throw new InvalidOperationException("Entity is not an editable path.");
        var geometry = entity.Geometry!;
        var entities = Content.Entities.ToArray();
        entities[index] = entity with
        {
            Geometry = geometry.Spline is not null
                ? geometry with { Spline = geometry.Spline with { Points = points.ToArray() } }
                : geometry with { GrindPath = geometry.GrindPath! with { Points = points.ToArray() } },
        };
        Content = Content with { Entities = entities };
    }

    public void RenameEntity(EntityId entityId, string name)
    {
        ForgeProjectValidation.ValidateText(name, nameof(name));
        UpdateEntities([entityId], entity => entity with { Name = name.Trim() });
    }

    public void SetEntityLayer(IReadOnlyList<EntityId> entityIds, string layer)
    {
        ForgeProjectValidation.ValidateText(layer, nameof(layer));
        UpdateEntities(entityIds, entity => entity with { Layer = layer.Trim() });
    }

    public void SetEntityState(
        IReadOnlyList<EntityId> entityIds,
        bool? hidden,
        bool? disabled,
        bool? locked) => UpdateEntities(entityIds, entity =>
    {
        var state = entity.State ?? new();
        return entity with
        {
            State = state with
            {
                Hidden = hidden ?? state.Hidden,
                Disabled = disabled ?? state.Disabled,
                Locked = locked ?? state.Locked,
            },
        };
    });

    public ProjectEntity RemoveEntity(EntityId entityId)
    {
        var index = FindEntityIndex(entityId);
        var removed = Content.Entities[index];
        Content = Content with { Entities = Content.Entities.Where(entity => entity.EntityId != entityId).ToArray() };
        return removed;
    }

    public EntityId[] RemoveEntities(IReadOnlyList<EntityId> entityIds)
    {
        var indexes = EntityIndexes(entityIds);
        var removed = entityIds.ToHashSet();
        var entities = Content.Entities.Where(entity => !removed.Contains(entity.EntityId)).ToArray();
        Content = Content with { Entities = entities };
        NormalizeSkyShellOrders();
        if (entities.Length == 0) return [];
        return [entities[Math.Min(indexes.Min(), entities.Length - 1)].EntityId];
    }

    public ProjectEntity[] GetEntities(IReadOnlyList<EntityId> entityIds)
    {
        _ = EntityIndexes(entityIds);
        var entities = Content.Entities.ToDictionary(entity => entity.EntityId);
        return entityIds.Select(id => entities[id]).ToArray();
    }

    public ProjectEntity[] AddCopies(IReadOnlyList<ProjectEntity> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);
        if (entities.Count == 0) throw new ArgumentException("At least one entity is required.", nameof(entities));
        var nextSkyOrder = Content.Entities.Count(entity => entity.SkyShell is not null);
        var copies = entities.Select(entity => entity with
        {
            EntityId = EntityId.New(),
            Name = entity.Name.Length <= 251 ? $"{entity.Name} copy" : $"{entity.Name[..251]} copy",
            Provenance = entity.SkyShell is null ? null : entity.Provenance,
            State = (entity.State ?? new()) with { Locked = false },
            Source = entity.Source is null ? null : entity.Source with
            {
                SourceIndex = entity.Source.SourceIndex ?? entity.Provenance?.SourceIndex,
            },
            SkyShell = entity.SkyShell is null
                ? null
                : entity.SkyShell with { Order = nextSkyOrder++ },
        }).ToArray();
        Content = Content with { Entities = Content.Entities.Concat(copies).ToArray() };
        return copies;
    }

    public void AddEntity(ProjectEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (Content.Entities.Any(value => value.EntityId == entity.EntityId))
            throw new InvalidOperationException($"Entity {entity.EntityId} already exists.");
        var previous = Content;
        try
        {
            Content = Content with { Entities = Content.Entities.Append(entity).ToArray() };
            Validate();
        }
        catch
        {
            Content = previous;
            throw;
        }
    }

    public void UpdateSkyShell(EntityId entityId, ProjectSkyShell skyShell)
    {
        ArgumentNullException.ThrowIfNull(skyShell);
        var index = FindEntityIndex(entityId);
        var entity = Content.Entities[index];
        if (entity.SkyShell is null) throw new InvalidOperationException("Entity is not a sky shell.");
        if (skyShell.SourceShellIndex != entity.SkyShell.SourceShellIndex || skyShell.Order != entity.SkyShell.Order)
            throw new InvalidOperationException("Sky shell source and order require their dedicated commands.");
        ForgeProjectValidation.ValidateSkyShell(entity with { SkyShell = skyShell });
        var entities = Content.Entities.ToArray();
        entities[index] = entity with { SkyShell = skyShell };
        Content = Content with { Entities = entities };
    }

    public void ReorderSkyShell(EntityId entityId, int destinationOrder)
    {
        var ordered = Content.Entities.Where(entity => entity.SkyShell is not null)
            .OrderBy(entity => entity.SkyShell!.Order).ToList();
        var entity = ordered.SingleOrDefault(value => value.EntityId == entityId)
            ?? throw new InvalidOperationException("Entity is not a sky shell.");
        if ((uint)destinationOrder >= (uint)ordered.Count)
            throw new ArgumentOutOfRangeException(nameof(destinationOrder));
        ordered.Remove(entity);
        ordered.Insert(destinationOrder, entity);
        ApplySkyShellOrders(ordered);
    }

    public void CompleteBaseEntityImport(IReadOnlyList<ProjectEntity> entities, int missingAssetCount)
    {
        ArgumentNullException.ThrowIfNull(entities);
        if (Manifest.BaseLevel.EntityVersion >= ProjectSchema.CurrentBaseEntityVersion) return;
        if (missingAssetCount < 0) throw new ArgumentOutOfRangeException(nameof(missingAssetCount));
        var importedBySource = entities.Where(entity => entity.Provenance is not null)
            .ToDictionary(entity => (entity.Provenance!.Section, entity.Provenance.SourceIndex));
        var existingSources = new HashSet<(string Section, int SourceIndex)>();
        var updated = Content.Entities.Select(entity =>
        {
            if (entity.Provenance is null) return entity;
            var key = (entity.Provenance.Section, entity.Provenance.SourceIndex);
            existingSources.Add(key);
            return importedBySource.TryGetValue(key, out var imported)
                ? entity with
                {
                    Name = imported.Geometry is null ? entity.Name : imported.Name,
                    Source = imported.Source,
                    Geometry = imported.Geometry,
                    Lighting = imported.Lighting,
                    TieLighting = imported.TieLighting,
                    Camera = imported.Camera,
                    AmbientSound = imported.AmbientSound,
                    SkyShell = imported.SkyShell,
                }
                : entity;
        });
        var merged = updated.Concat(entities.Where(entity => entity.Provenance is not null
            && existingSources.Add((entity.Provenance.Section, entity.Provenance.SourceIndex)))).ToArray();
        Content = Content with { Entities = RemapGeometryLinks(merged) };
        Manifest = Manifest with
        {
            BaseLevel = Manifest.BaseLevel with
            {
                MissingAssetCount = missingAssetCount,
                EntityVersion = ProjectSchema.CurrentBaseEntityVersion,
            },
        };
    }

    private static ProjectEntity[] RemapGeometryLinks(ProjectEntity[] entities)
    {
        var cuboids = entities.Where(entity => entity.Geometry?.Cuboid is not null && entity.Provenance is not null)
            .ToDictionary(entity => entity.Provenance!.SourceIndex, entity => entity.EntityId);
        var spheres = entities.Where(entity => entity.Geometry?.Sphere is not null && entity.Provenance is not null)
            .ToDictionary(entity => entity.Provenance!.SourceIndex, entity => entity.EntityId);
        var cylinders = entities.Where(entity => entity.Geometry?.Cylinder is not null && entity.Provenance is not null)
            .ToDictionary(entity => entity.Provenance!.SourceIndex, entity => entity.EntityId);
        var splines = entities.Where(entity => entity.Geometry?.Spline is not null && entity.Provenance is not null)
            .ToDictionary(entity => entity.Provenance!.SourceIndex, entity => entity.EntityId);
        return entities.Select(entity => entity.Geometry?.Area is not { } area ? entity : entity with
        {
            Geometry = entity.Geometry with
            {
                Area = area with
                {
                    Splines = Remap(area.Splines, splines),
                    Cuboids = Remap(area.Cuboids, cuboids),
                    Spheres = Remap(area.Spheres, spheres),
                    Cylinders = Remap(area.Cylinders, cylinders),
                    NegativeCuboids = Remap(area.NegativeCuboids, cuboids),
                },
            },
        }).ToArray();
    }

    private static ProjectGeometryLink[] Remap(
        IReadOnlyList<ProjectGeometryLink> links,
        IReadOnlyDictionary<int, EntityId> entities) => links
        .Select(link => link with { EntityId = entities.GetValueOrDefault(link.SourceIndex) })
        .ToArray();

    public void Rename(string name)
    {
        ForgeProjectValidation.ValidateText(name, nameof(name));
        Manifest = Manifest with { Name = name.Trim() };
    }

    public async Task<ProjectAssetEdit> ApplyAssetEditAsync(
        EntityId entityId,
        ReadOnlyMemory<byte> canonicalBytes,
        AssetCatalogStore globalCatalog,
        bool makeUnique = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(globalCatalog);
        if (canonicalBytes.IsEmpty) throw new ArgumentException("Edited asset bytes cannot be empty.", nameof(canonicalBytes));
        var selected = Content.Entities[FindEntityIndex(entityId)];
        if (selected.SkyShell is not null)
            throw new InvalidOperationException("Sky shell source assets are immutable.");
        var source = selected.Asset ?? throw new InvalidOperationException($"Entity {entityId} does not reference an asset.");
        var canonicalFormatVersion = ResolveAssetDetails(source, globalCatalog);
        var derivedId = AssetId.Compute(source.Kind, canonicalFormatVersion, canonicalBytes.Span);
        if (derivedId == source.Id) throw new InvalidOperationException("The edited asset is identical to its source.");

        var attached = new ProjectAttachedAsset(
            derivedId, source.Kind, canonicalFormatVersion, source.Id, canonicalBytes.Length);
        var existing = Content.Assets.SingleOrDefault(asset => asset.Id == derivedId);
        if (existing is not null && (existing.Kind != attached.Kind
            || existing.CanonicalFormatVersion != attached.CanonicalFormatVersion
            || existing.Size != attached.Size))
            throw new InvalidDataException($"Project asset {derivedId} has inconsistent metadata.");
        await EnsureAssetBlobAsync(attached, canonicalBytes, cancellationToken);

        var current = new ProjectAssetReference(derivedId, source.Kind);
        var changes = Content.Entities
            .Where(entity => entity.Asset == source && (entity.EntityId == entityId || !makeUnique))
            .Select(entity => new ProjectAssetReferenceChange(entity.EntityId, source, current))
            .ToArray();
        var changedIds = changes.Select(change => change.EntityId).ToHashSet();
        Content = Content with
        {
            Entities = Content.Entities.Select(entity => changedIds.Contains(entity.EntityId)
                ? entity with { Asset = current }
                : entity).ToArray(),
            Assets = existing is null ? Content.Assets.Append(attached).ToArray() : Content.Assets,
        };
        return new(derivedId, changes);
    }

    public void UndoAssetEdit(ProjectAssetEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var changes = edit.Changes.ToDictionary(change => change.EntityId);
        foreach (var change in changes.Values)
        {
            var entity = Content.Entities[FindEntityIndex(change.EntityId)];
            if (entity.Asset != change.Current)
                throw new InvalidOperationException($"Entity {change.EntityId} no longer has the asset reference produced by this edit.");
        }
        Content = Content with
        {
            Entities = Content.Entities.Select(entity => changes.TryGetValue(entity.EntityId, out var change)
                ? entity with { Asset = change.Previous }
                : entity).ToArray(),
        };
    }

    public bool IsAssetReferenced(AssetId id) => Content.Entities.Any(entity => entity.Asset?.Id == id);

    public string? ResolveAssetPath(AssetId id, AssetCatalogStore globalCatalog)
    {
        ArgumentNullException.ThrowIfNull(globalCatalog);
        if (Content.Assets.Any(asset => asset.Id == id))
        {
            var projectPath = AssetBlobPath(id);
            if (File.Exists(projectPath)) return projectPath;
        }
        return globalCatalog.ResolveBlobPath(id);
    }

    private uint ResolveAssetDetails(
        ProjectAssetReference reference,
        AssetCatalogStore globalCatalog)
    {
        var attached = Content.Assets.SingleOrDefault(asset => asset.Id == reference.Id);
        if (attached is not null)
        {
            if (attached.Kind != reference.Kind) throw new InvalidDataException($"Asset {reference.Id} kind does not match its entity reference.");
            if (!File.Exists(AssetBlobPath(reference.Id))) throw new FileNotFoundException($"Project asset {reference.Id} is missing.");
            return attached.CanonicalFormatVersion;
        }
        var global = globalCatalog.Query(new(Id: reference.Id)).SingleOrDefault()
            ?? throw new FileNotFoundException($"Global asset {reference.Id} is missing.");
        if (global.Kind != reference.Kind) throw new InvalidDataException($"Asset {reference.Id} kind does not match its entity reference.");
        return global.CanonicalFormatVersion;
    }

    private async Task EnsureAssetBlobAsync(
        ProjectAttachedAsset asset,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        var path = AssetBlobPath(asset.Id);
        if (File.Exists(path))
        {
            if (new FileInfo(path).Length != asset.Size
                || AssetId.Compute(asset.Kind, asset.CanonicalFormatVersion, await File.ReadAllBytesAsync(path, cancellationToken)) != asset.Id)
                throw new InvalidDataException($"Project asset blob {asset.Id} failed integrity verification.");
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.partial";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes.ToArray(), cancellationToken);
            File.Move(temporary, path);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private string AssetBlobPath(AssetId id)
    {
        var value = id.ToString();
        return Path.Combine(RootPath, "assets", value[..2], $"{value}.blob");
    }

    private int FindEntityIndex(EntityId id)
    {
        for (var index = 0; index < Content.Entities.Count; index++)
            if (Content.Entities[index].EntityId == id) return index;
        throw new KeyNotFoundException($"Entity {id} is not present in the project.");
    }

    private int[] EntityIndexes(IReadOnlyList<EntityId> entityIds)
    {
        ArgumentNullException.ThrowIfNull(entityIds);
        if (entityIds.Count == 0 || entityIds.Distinct().Count() != entityIds.Count)
            throw new ArgumentException("Entity IDs must be non-empty and unique.", nameof(entityIds));
        return entityIds.Select(FindEntityIndex).ToArray();
    }

    private void UpdateEntities(IReadOnlyList<EntityId> entityIds, Func<ProjectEntity, ProjectEntity> update)
    {
        ArgumentNullException.ThrowIfNull(entityIds);
        ArgumentNullException.ThrowIfNull(update);
        var ids = entityIds.ToHashSet();
        if (ids.Count != entityIds.Count || ids.Count == 0)
            throw new ArgumentException("Entity IDs must be non-empty and unique.", nameof(entityIds));
        foreach (var id in ids) _ = FindEntityIndex(id);
        Content = Content with
        {
            Entities = Content.Entities.Select(entity => ids.Contains(entity.EntityId) ? update(entity) : entity).ToArray(),
        };
    }

    private void Validate() => ForgeProjectValidation.Validate(RootPath, Manifest, Content);

    private void NormalizeSkyShellOrders()
    {
        ApplySkyShellOrders(Content.Entities.Where(entity => entity.SkyShell is not null)
            .OrderBy(entity => entity.SkyShell!.Order).ToArray());
    }

    private void ApplySkyShellOrders(IReadOnlyList<ProjectEntity> ordered)
    {
        var orders = ordered.Select((entity, order) => (entity.EntityId, Order: order))
            .ToDictionary(value => value.EntityId, value => value.Order);
        Content = Content with
        {
            Entities = Content.Entities.Select(entity => entity.SkyShell is not null
                ? entity with { SkyShell = entity.SkyShell with { Order = orders[entity.EntityId] } }
                : entity).ToArray(),
        };
    }

}

internal sealed record ForgeProjectState(ForgeProjectManifest Manifest, ForgeProjectContent Content);
