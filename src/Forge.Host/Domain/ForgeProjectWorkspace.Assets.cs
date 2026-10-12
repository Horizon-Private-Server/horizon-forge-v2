namespace Forge.Host.Domain;

public sealed partial class ForgeProjectWorkspace
{
    public Task<ProjectAttachedAsset> AttachTextureAssetAsync(
        ReadOnlyMemory<byte> canonicalBytes,
        AssetId? parentId = null,
        CancellationToken cancellationToken = default)
    {
        if (canonicalBytes.Length > ProjectTextureAssetSchema.MaximumCanonicalBytes)
            throw new ArgumentException(
                $"Texture asset exceeds {ProjectTextureAssetSchema.MaximumCanonicalBytes} bytes.",
                nameof(canonicalBytes));

        var id = AssetId.Compute(
            AssetKind.Texture,
            ProjectTextureAssetSchema.CanonicalFormatVersion,
            canonicalBytes.Span);
        var existing = Content.Assets.SingleOrDefault(value => value.Id == id);
        return AttachAssetAsync(
            AssetKind.Texture,
            ProjectTextureAssetSchema.CanonicalFormatVersion,
            canonicalBytes,
            existing?.ParentId ?? parentId,
            cancellationToken);
    }

    public async Task<ProjectAttachedAsset> AttachAssetAsync(
        AssetKind kind,
        uint canonicalFormatVersion,
        ReadOnlyMemory<byte> canonicalBytes,
        AssetId? parentId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var attached = CreateAttachedAsset(
            kind, canonicalFormatVersion, canonicalBytes, parentId, nameof(canonicalBytes), nameof(parentId));
        var id = attached.Id;
        var existing = Content.Assets.SingleOrDefault(asset => asset.Id == id);
        if (existing is not null && existing != attached)
            throw new InvalidDataException($"Project asset {id} has inconsistent metadata.");

        await EnsureAssetBlobAsync(attached, canonicalBytes, cancellationToken);
        if (existing is not null) return existing;
        var content = Content with { Assets = Content.Assets.Append(attached).ToArray() };
        ForgeProjectValidation.Validate(RootPath, Manifest, content);
        Content = content;
        return attached;
    }

    internal async Task ImportTfragsAsync(
        ProjectTfragState state,
        IReadOnlyList<ProjectEntity> entities,
        IReadOnlyList<ProjectAssetAttachment> assets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(assets);
        if (Content.Tfrags is not null)
            throw new InvalidOperationException("Project tfrag pieces are already imported.");
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = assets.Select(attachment =>
        {
            ArgumentNullException.ThrowIfNull(attachment);
            return (Asset: CreateAttachedAsset(
                    attachment.Kind,
                    attachment.CanonicalFormatVersion,
                    attachment.CanonicalBytes,
                    attachment.ParentId,
                    nameof(assets),
                    nameof(assets)),
                Bytes: attachment.CanonicalBytes);
        }).ToArray();
        if (resolved.Select(value => value.Asset.Id).Distinct().Count() != resolved.Length)
            throw new ArgumentException("Attached asset batch contains duplicate content.", nameof(assets));
        var existing = Content.Assets.ToDictionary(asset => asset.Id);
        foreach (var value in resolved)
        {
            if (existing.TryGetValue(value.Asset.Id, out var current) && current != value.Asset)
                throw new InvalidDataException($"Project asset {value.Asset.Id} has inconsistent metadata.");
        }
        var added = resolved.Where(value => !existing.ContainsKey(value.Asset.Id)).Select(value => value.Asset);
        var content = Content with
        {
            Assets = Content.Assets.Concat(added)
                .OrderBy(asset => asset.Id.ToString(), StringComparer.Ordinal).ToArray(),
            Entities = Content.Entities.Concat(entities).ToArray(),
            Tfrags = state,
        };
        ForgeProjectValidation.Validate(RootPath, Manifest, content);
        foreach (var value in resolved)
            await EnsureAssetBlobAsync(value.Asset, value.Bytes, cancellationToken);
        Content = content;
    }

    private static ProjectAttachedAsset CreateAttachedAsset(
        AssetKind kind,
        uint canonicalFormatVersion,
        ReadOnlyMemory<byte> canonicalBytes,
        AssetId? parentId,
        string bytesParameterName,
        string parentParameterName)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown asset kind.");
        if (canonicalBytes.IsEmpty)
            throw new ArgumentException("Attached asset bytes cannot be empty.", bytesParameterName);
        if (canonicalBytes.Length > MaxAttachedAssetBytes)
            throw new ArgumentException($"Attached asset exceeds {MaxAttachedAssetBytes} bytes.", bytesParameterName);
        if (parentId is { } parent && parent.ToString().Length != AssetId.TextLength)
            throw new ArgumentException("Parent Asset ID is invalid.", parentParameterName);
        var id = AssetId.Compute(kind, canonicalFormatVersion, canonicalBytes.Span);
        if (id == parentId)
            throw new ArgumentException("An attached asset cannot derive from itself.", parentParameterName);
        return new(id, kind, canonicalFormatVersion, parentId, canonicalBytes.Length);
    }

    internal int RefreshTfragMetadata(IReadOnlyList<ProjectEntity> sourceEntities)
    {
        ArgumentNullException.ThrowIfNull(sourceEntities);
        var sourceByOccurrence = sourceEntities.Where(entity => entity.Tfrag is not null)
            .ToDictionary(entity => entity.Tfrag!.OccurrenceKey, StringComparer.Ordinal);
        var changed = 0;
        var entities = Content.Entities.Select(entity =>
        {
            if (entity.Tfrag is not { } current
                || current.SourcePosition is not null && current.Bounds is not null && current.Diagnostics is not null)
                return entity;
            if (!sourceByOccurrence.TryGetValue(current.OccurrenceKey, out var source)
                || source.Tfrag is null || source.Asset != entity.Asset)
                throw new InvalidDataException($"Tfrag entity {entity.EntityId} does not match verified source data.");
            changed++;
            return entity with { Tfrag = source.Tfrag };
        }).ToArray();
        if (changed == 0) return 0;
        var previous = Content;
        Content = Content with { Entities = entities };
        try
        {
            Validate();
            return changed;
        }
        catch
        {
            Content = previous;
            throw;
        }
    }

    public ProjectAssetOverride SetAssetOverride(
        ProjectAssetReference source,
        ProjectAssetReference replacement)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(replacement);
        var binding = new ProjectAssetOverride(ProjectAssetOverrideSchema.CurrentVersion, source, replacement);
        var content = Content with
        {
            AssetOverrides = Content.AssetOverrides
                .Where(value => value.Source != source)
                .Append(binding)
                .OrderBy(value => value.Source.Kind)
                .ThenBy(value => value.Source.Id.ToString(), StringComparer.Ordinal)
                .ToArray(),
        };
        ForgeProjectValidation.Validate(RootPath, Manifest, content);
        Content = content;
        return binding;
    }

    public ProjectAssetOverride RemoveAssetOverride(ProjectAssetReference source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var binding = Content.AssetOverrides.SingleOrDefault(value => value.Source == source)
            ?? throw new KeyNotFoundException($"Asset {source.Id} has no override binding.");
        Content = Content with
        {
            AssetOverrides = Content.AssetOverrides.Where(value => value != binding).ToArray(),
        };
        return binding;
    }

    public ProjectAssetReference ResolveAssetReference(ProjectAssetReference source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Content.AssetOverrides.SingleOrDefault(value => value.Source == source)?.Replacement ?? source;
    }

    public ProjectResolvedAsset? ResolveAsset(
        ProjectAssetReference source,
        AssetCatalogStore globalCatalog)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(globalCatalog);
        var effective = ResolveAssetReference(source);
        var attached = Content.Assets.SingleOrDefault(asset => asset.Id == effective.Id);
        if (attached is not null)
        {
            if (attached.Kind != effective.Kind)
                throw new InvalidDataException($"Asset {effective.Id} kind does not match its project reference.");
            var path = ResolveAttachedAssetPath(effective.Id);
            return path is null ? null : new(
                source, effective, attached.CanonicalFormatVersion, attached.Size, path, true);
        }
        var global = globalCatalog.Query(new(Id: effective.Id)).SingleOrDefault();
        if (global is null) return null;
        if (global.Kind != effective.Kind)
            throw new InvalidDataException($"Asset {effective.Id} kind does not match its catalog reference.");
        var globalPath = globalCatalog.ResolveBlobPath(effective.Id);
        return globalPath is null ? null : new(
            source, effective, global.CanonicalFormatVersion, global.Size, globalPath, false);
    }

    internal Task<byte[]> ReadAttachedAssetVerifiedAsync(
        ProjectAssetReference reference,
        long maxBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var asset = Content.Assets.SingleOrDefault(value => value.Id == reference.Id)
            ?? throw new FileNotFoundException($"Project asset {reference.Id} is not attached.");
        if (asset.Kind != reference.Kind)
            throw new InvalidDataException($"Project asset {reference.Id} kind does not match its reference.");
        return AssetCatalogBlobReader.ReadVerifiedAsync(
            asset.Id,
            asset.Kind,
            asset.CanonicalFormatVersion,
            asset.Size,
            AssetBlobPath(asset.Id),
            maxBytes,
            cancellationToken);
    }
}
