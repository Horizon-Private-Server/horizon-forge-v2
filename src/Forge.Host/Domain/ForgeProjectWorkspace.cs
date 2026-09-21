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

    public static async Task<ForgeProjectWorkspace> CreateAsync(
        string rootPath,
        string name,
        ProjectTargetProfile target,
        ProjectBaseLevel baseLevel,
        IReadOnlyList<ProjectEntity> entities,
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
            new(ProjectSchema.CurrentVersion, ProjectSchema.ContentDocumentType, entities.ToArray(), []));
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
        ValidateTransform(transform);
        var index = FindEntityIndex(entityId);
        var entities = Content.Entities.ToArray();
        entities[index] = entities[index] with { Transform = transform };
        Content = Content with { Entities = entities };
    }

    public void RenameEntity(EntityId entityId, string name)
    {
        ValidateText(name, nameof(name));
        UpdateEntities([entityId], entity => entity with { Name = name.Trim() });
    }

    public void SetEntityLayer(IReadOnlyList<EntityId> entityIds, string layer)
    {
        ValidateText(layer, nameof(layer));
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
            return entity.Source is null && importedBySource.TryGetValue(key, out var imported)
                ? entity with { Source = imported.Source }
                : entity;
        });
        Content = Content with
        {
            Entities = updated.Concat(entities.Where(entity => entity.Provenance is not null
                && existingSources.Add((entity.Provenance.Section, entity.Provenance.SourceIndex)))).ToArray(),
        };
        Manifest = Manifest with
        {
            BaseLevel = Manifest.BaseLevel with
            {
                MissingAssetCount = missingAssetCount,
                EntityVersion = ProjectSchema.CurrentBaseEntityVersion,
            },
        };
    }

    public void Rename(string name)
    {
        ValidateText(name, nameof(name));
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

    private void Validate()
    {
        if (Manifest.SchemaVersion != ProjectSchema.CurrentVersion) throw new UnsupportedProjectSchemaException(Manifest.SchemaVersion);
        if (Content.SchemaVersion != ProjectSchema.CurrentVersion) throw new UnsupportedProjectSchemaException(Content.SchemaVersion);
        if (Manifest.DocumentType != ProjectSchema.ManifestDocumentType) throw new InvalidDataException("Project manifest document type is invalid.");
        if (Content.DocumentType != ProjectSchema.ContentDocumentType) throw new InvalidDataException("Project content document type is invalid.");
        if (Manifest.ProjectId.Value == Guid.Empty) throw new InvalidDataException("Project ID cannot be empty.");
        if (Manifest.Target is null || Manifest.BaseLevel is null) throw new InvalidDataException("Project target and base level are required.");
        if (Content.Entities is null || Content.Assets is null) throw new InvalidDataException("Project content lists are required.");
        ValidateText(Manifest.Name, nameof(Manifest.Name));
        ValidateText(Manifest.Target.Game, nameof(Manifest.Target.Game));
        ValidateText(Manifest.Target.Region, nameof(Manifest.Target.Region));
        ValidateText(Manifest.Target.Revision, nameof(Manifest.Target.Revision));
        ValidateText(Manifest.Target.BakeProfile, nameof(Manifest.Target.BakeProfile));
        ValidateText(Manifest.BaseLevel.Game, nameof(Manifest.BaseLevel.Game));
        ValidateText(Manifest.BaseLevel.Region, nameof(Manifest.BaseLevel.Region));
        ValidateText(Manifest.BaseLevel.Revision, nameof(Manifest.BaseLevel.Revision));
        if (Manifest.BaseLevel.Level < 0) throw new InvalidDataException("Base level cannot be negative.");
        if (Manifest.BaseLevel.MissingAssetCount < 0) throw new InvalidDataException("Base-level missing asset count cannot be negative.");
        if (Manifest.BaseLevel.SourceFingerprint is null
            || Manifest.BaseLevel.SourceFingerprint.Length != 32
            || !Manifest.BaseLevel.SourceFingerprint.All(Uri.IsHexDigit))
            throw new InvalidDataException("Base-level source fingerprint must be a 32-character MD5 value.");
        _ = ContentFilePath;

        if (Content.Entities.Any(entity => entity is null)) throw new InvalidDataException("Project entities cannot contain null entries.");
        if (Content.Entities.Select(entity => entity.EntityId).Distinct().Count() != Content.Entities.Count)
            throw new InvalidDataException("Project contains duplicate Entity IDs.");
        foreach (var entity in Content.Entities)
        {
            if (entity is null || entity.EntityId.Value == Guid.Empty) throw new InvalidDataException("Project entity ID cannot be empty.");
            ValidateText(entity.Name, nameof(entity.Name));
            ValidateText(entity.Layer, nameof(entity.Layer));
            if (entity.Transform is null) throw new InvalidDataException($"Entity {entity.EntityId} transform is required.");
            ValidateTransform(entity.Transform);
            if (entity.Asset is not null)
            {
                if (entity.Asset.Id.ToString().Length != AssetId.TextLength)
                    throw new InvalidDataException($"Entity {entity.EntityId} has an empty Asset ID.");
                if (!Enum.IsDefined(entity.Asset.Kind)) throw new InvalidDataException($"Entity {entity.EntityId} has an unknown asset kind.");
            }
            if (entity.Provenance is not null)
            {
                ValidateText(entity.Provenance.Game, nameof(entity.Provenance.Game));
                ValidateText(entity.Provenance.Section, nameof(entity.Provenance.Section));
                if (entity.Provenance.Level < 0 || entity.Provenance.SourceIndex < 0)
                    throw new InvalidDataException($"Entity {entity.EntityId} provenance indexes cannot be negative.");
            }
            if (entity.Source is not null)
            {
                if (entity.Source.ClassId < 0 || entity.Source.RawRecord is null || entity.Source.RawRecord.Length is 0 or > 4_096)
                    throw new InvalidDataException($"Entity {entity.EntityId} source record is invalid.");
            }
        }
        if (Content.Assets.Any(asset => asset is null)) throw new InvalidDataException("Project assets cannot contain null entries.");
        if (Content.Assets.Select(asset => asset.Id).Distinct().Count() != Content.Assets.Count)
            throw new InvalidDataException("Project contains duplicate attached Asset IDs.");
        foreach (var asset in Content.Assets)
        {
            if (asset is null || asset.Id.ToString().Length != AssetId.TextLength || asset.ParentId.ToString().Length != AssetId.TextLength)
                throw new InvalidDataException("Project asset IDs cannot be empty.");
            if (!Enum.IsDefined(asset.Kind)) throw new InvalidDataException($"Project asset {asset.Id} has an unknown kind.");
            if (asset.Size < 1) throw new InvalidDataException($"Project asset {asset.Id} has an invalid size.");
            if (asset.Id == asset.ParentId) throw new InvalidDataException($"Project asset {asset.Id} cannot derive from itself.");
        }
    }

    private static void ValidateTransform(ProjectTransform transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        if (transform.Position is null || transform.Rotation is null || transform.Scale is null)
            throw new InvalidDataException("Entity transform components are required.");
        var values = new[]
        {
            transform.Position.X, transform.Position.Y, transform.Position.Z,
            transform.Rotation.X, transform.Rotation.Y, transform.Rotation.Z, transform.Rotation.W,
            transform.Scale.X, transform.Scale.Y, transform.Scale.Z,
        };
        if (values.Any(value => !float.IsFinite(value))) throw new InvalidDataException("Entity transform values must be finite.");
        if (transform.Rotation is { X: 0, Y: 0, Z: 0, W: 0 }) throw new InvalidDataException("Entity rotation cannot be an empty quaternion.");
        if (transform.Scale.X == 0 || transform.Scale.Y == 0 || transform.Scale.Z == 0)
            throw new InvalidDataException("Entity scale cannot contain zero.");
    }

    private static void ValidateText(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256)
            throw new InvalidDataException($"{name} must contain between 1 and 256 characters.");
    }

}
