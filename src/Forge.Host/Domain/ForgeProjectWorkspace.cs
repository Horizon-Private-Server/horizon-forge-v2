namespace Forge.Host.Domain;

public sealed partial class ForgeProjectWorkspace
{
    public const string ManifestFileName = "forge-project.json";
    public const string DefaultContentPath = "content/project.json.gz";
    public const string RecoveryDirectoryName = ForgeProjectPersistence.RecoveryDirectoryName;
    public const int MaxRecoverySnapshots = ForgeProjectPersistence.MaxRecoverySnapshots;
    public const long MaxRecoveryBytes = ForgeProjectPersistence.MaxRecoveryBytes;
    private string _savedFingerprint;

    private ForgeProjectWorkspace(
        string rootPath,
        ForgeProjectManifest manifest,
        ForgeProjectContent content)
    {
        RootPath = rootPath;
        Manifest = manifest;
        Content = content;
        _savedFingerprint = ForgeProjectPersistence.Fingerprint(manifest, content);
    }

    public string RootPath { get; }
    public ForgeProjectManifest Manifest { get; private set; }
    public ForgeProjectContent Content { get; private set; }
    public string ContentFilePath => ForgeProjectPersistence.ResolveRelativePath(RootPath, Manifest.Content);
    public string CurrentFingerprint => ForgeProjectPersistence.Fingerprint(Manifest, Content);
    public bool IsDirty => CurrentFingerprint != _savedFingerprint;

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
            new(ProjectSchema.CurrentVersion, ProjectSchema.ContentDocumentType, entities.ToArray(), [], [], levelSettings));
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
        var workspace = new ForgeProjectWorkspace(root, loaded.Manifest, loaded.Content);
        workspace.Validate();
        return workspace;
    }

    public static async Task<ForgeProjectSummary> SummarizeAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(rootPath);
        var manifest = await ForgeProjectPersistence.LoadManifestAsync(root, cancellationToken);
        var manifestPath = Path.Combine(root, ManifestFileName);
        var contentPath = ForgeProjectPersistence.ResolveRelativePath(root, manifest.Content);
        if (!File.Exists(contentPath)) throw new InvalidDataException("Project content is missing.");
        var modified = new[] { File.GetLastWriteTimeUtc(manifestPath), File.GetLastWriteTimeUtc(contentPath) }.Max();
        var modifiedUnixMilliseconds = new DateTimeOffset(modified).ToUnixTimeMilliseconds();
        var recoveryRoot = Path.Combine(root, RecoveryDirectoryName);
        var hasRecovery = Directory.Exists(recoveryRoot)
            && Directory.EnumerateDirectories(recoveryRoot)
                .Select(Path.GetFileName)
                .Any(value => ForgeProjectPersistence.TryGetRecoveryCreated(value, out var created)
                    && created > modifiedUnixMilliseconds);
        return new(
            root,
            manifest.Name,
            manifest.Target.Game,
            manifest.Target.Region,
            manifest.Target.Revision,
            manifest.Target.BakeProfile,
            manifest.BaseLevel.Level,
            modifiedUnixMilliseconds,
            hasRecovery);
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        var previousManifest = Manifest;
        var previousContent = Content;
        try
        {
            Manifest = Manifest with { SchemaVersion = ProjectSchema.CurrentVersion, Content = DefaultContentPath };
            Content = Content with { SchemaVersion = ProjectSchema.CurrentVersion };
            Validate();
            await ForgeProjectPersistence.SaveAsync(RootPath, Manifest, Content, cancellationToken);
            _savedFingerprint = CurrentFingerprint;
        }
        catch
        {
            Manifest = previousManifest;
            Content = previousContent;
            throw;
        }
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
        try
        {
            Manifest = loaded.Manifest;
            Content = loaded.Content;
            Validate();
        }
        catch
        {
            Manifest = previousManifest;
            Content = previousContent;
            throw;
        }
    }

    public void UpdateTransform(EntityId entityId, ProjectTransform transform)
        => UpdateTransforms([new(entityId, transform)]);

    public void UpdateTransforms(IReadOnlyList<EditorTransformUpdate> updates)
    {
        ArgumentNullException.ThrowIfNull(updates);
        var entities = Content.Entities.ToDictionary(entity => entity.EntityId);
        foreach (var update in updates)
        {
            ForgeProjectValidation.ValidateTransform(update.Transform);
            if (!entities.ContainsKey(update.EntityId))
                throw new KeyNotFoundException($"Entity {update.EntityId} is not present in the project.");
        }
        var transforms = updates.ToDictionary(update => update.EntityId, update => update.Transform);
        var parentDeltas = new Dictionary<EntityId, ProjectVector3>();
        foreach (var update in updates)
        {
            var entity = entities[update.EntityId];
            if (entity.Collision?.Attachment is not { } attachment) continue;
            transforms.Remove(update.EntityId);
            if (transforms.ContainsKey(attachment.ParentEntityId)) continue;
            var delta = new ProjectVector3(
                update.Transform.Position.X - entity.Transform.Position.X,
                update.Transform.Position.Y - entity.Transform.Position.Y,
                update.Transform.Position.Z - entity.Transform.Position.Z);
            if (parentDeltas.TryGetValue(attachment.ParentEntityId, out var existing) && existing != delta)
                throw new ArgumentException("Linked collision pieces require the same parent translation.", nameof(updates));
            parentDeltas[attachment.ParentEntityId] = delta;
        }
        foreach (var (parentId, delta) in parentDeltas)
        {
            var parent = entities[parentId].Transform;
            transforms[parentId] = parent with
            {
                Position = new(
                    parent.Position.X + delta.X,
                    parent.Position.Y + delta.Y,
                    parent.Position.Z + delta.Z),
            };
        }
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
        Content = Content with
        {
            Entities = Content.Entities.Where(entity => entity.EntityId != entityId).ToArray(),
            InstancedCollisionBindings = Content.InstancedCollisionBindings
                .Where(binding => binding.InstanceEntityId != entityId).ToArray(),
        };
        return removed;
    }

    public EntityId[] RemoveEntities(IReadOnlyList<EntityId> entityIds)
    {
        var indexes = EntityIndexes(entityIds);
        var removed = entityIds.ToHashSet();
        foreach (var collision in Content.Entities.Where(entity =>
            entity.Collision?.Attachment is { } attachment && removed.Contains(attachment.ParentEntityId)))
            removed.Add(collision.EntityId);
        var entities = Content.Entities.Where(entity => !removed.Contains(entity.EntityId)).ToArray();
        Content = Content with
        {
            Entities = entities,
            InstancedCollisionBindings = Content.InstancedCollisionBindings
                .Where(binding => binding.InstanceEntityId is null || !removed.Contains(binding.InstanceEntityId.Value))
                .ToArray(),
        };
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
            || existing.ParentId != attached.ParentId
            || existing.Size != attached.Size))
            throw new InvalidDataException($"Project asset {derivedId} has inconsistent metadata.");
        var current = new ProjectAssetReference(derivedId, source.Kind);
        var changes = Content.Entities
            .Where(entity => entity.Asset == source && (entity.EntityId == entityId || !makeUnique))
            .Select(entity => new ProjectAssetReferenceChange(entity.EntityId, source, current))
            .ToArray();
        var changedIds = changes.Select(change => change.EntityId).ToHashSet();
        if (Content.InstancedCollisionBindings.Any(binding =>
            binding.InstanceEntityId is { } instanceEntityId && changedIds.Contains(instanceEntityId)))
            throw new InvalidOperationException(
                "Remove the individual collision before editing its source asset.");
        await EnsureAssetBlobAsync(attached, canonicalBytes, cancellationToken);
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

    public bool IsAssetReferenced(AssetId id) => Content.Entities.Any(entity => entity.Asset?.Id == id)
        || Content.InstancedCollisionBindings.Any(binding => binding.ProxyAssetId == id);

    public async Task<IReadOnlyList<AssetId>> CollectUnreferencedAssetsAsync(
        CancellationToken cancellationToken = default)
    {
        if (IsDirty) throw new InvalidOperationException("Save the project before collecting attached assets.");

        var contents = new List<ForgeProjectContent> { Content };
        foreach (var recovery in await ListRecoveriesAsync(cancellationToken))
        {
            var loaded = await ForgeProjectPersistence.LoadRecoveryAsync(RootPath, recovery.Id, cancellationToken);
            if (loaded.Manifest.ProjectId != Manifest.ProjectId)
                throw new InvalidDataException("A recovery snapshot belongs to a different project.");
            contents.Add(loaded.Content);
        }

        var referenced = contents
            .SelectMany(content => content.Entities)
            .Where(entity => entity.Asset is not null)
            .Select(entity => entity.Asset!.Id)
            .Concat(contents.SelectMany(content => content.InstancedCollisionBindings)
                .Select(binding => binding.ProxyAssetId))
            .ToHashSet();
        var assets = contents.SelectMany(content => content.Assets).ToArray();
        while (assets.Where(asset => referenced.Contains(asset.Id))
            .Select(asset => asset.ParentId)
            .Any(referenced.Add)) { }

        var candidates = Content.Assets.Where(asset => !referenced.Contains(asset.Id)).ToArray();
        if (candidates.Length == 0) return [];

        cancellationToken.ThrowIfCancellationRequested();
        foreach (var candidate in candidates) File.Delete(AssetBlobPath(candidate.Id));
        var previousContent = Content;
        try
        {
            Content = Content with
            {
                Assets = Content.Assets.Where(asset => referenced.Contains(asset.Id)).ToArray(),
            };
            await SaveAsync(cancellationToken);
        }
        catch
        {
            Content = previousContent;
            throw;
        }
        return candidates.Select(asset => asset.Id).ToArray();
    }

    internal string? ResolveAttachedAssetPath(AssetId id)
    {
        if (!Content.Assets.Any(asset => asset.Id == id)) return null;
        var path = AssetBlobPath(id);
        return File.Exists(path) ? path : null;
    }

    public string? ResolveAssetPath(AssetId id, AssetCatalogStore globalCatalog)
    {
        ArgumentNullException.ThrowIfNull(globalCatalog);
        return ResolveAttachedAssetPath(id) ?? globalCatalog.ResolveBlobPath(id);
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
