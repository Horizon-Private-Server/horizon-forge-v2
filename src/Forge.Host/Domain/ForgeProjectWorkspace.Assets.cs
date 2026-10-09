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
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown asset kind.");
        if (canonicalBytes.IsEmpty) throw new ArgumentException("Attached asset bytes cannot be empty.", nameof(canonicalBytes));
        if (canonicalBytes.Length > MaxAttachedAssetBytes)
            throw new ArgumentException($"Attached asset exceeds {MaxAttachedAssetBytes} bytes.", nameof(canonicalBytes));
        if (parentId is { } parent && parent.ToString().Length != AssetId.TextLength)
            throw new ArgumentException("Parent Asset ID is invalid.", nameof(parentId));

        var id = AssetId.Compute(kind, canonicalFormatVersion, canonicalBytes.Span);
        if (id == parentId) throw new ArgumentException("An attached asset cannot derive from itself.", nameof(parentId));
        var attached = new ProjectAttachedAsset(id, kind, canonicalFormatVersion, parentId, canonicalBytes.Length);
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
