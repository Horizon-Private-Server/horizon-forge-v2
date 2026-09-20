using System.Text.Json;
using System.Text.Json.Serialization;

namespace Forge.Host.Domain;

public sealed class ForgeProjectWorkspace
{
    public const string ManifestFileName = "forge-project.json";
    public const string DefaultContentPath = "content/project.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        MaxDepth = 64,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() },
    };

    private ForgeProjectWorkspace(string rootPath, ForgeProjectManifest manifest, ForgeProjectContent content)
    {
        RootPath = rootPath;
        Manifest = manifest;
        Content = content;
    }

    public string RootPath { get; }
    public ForgeProjectManifest Manifest { get; private set; }
    public ForgeProjectContent Content { get; private set; }

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
            new(ProjectSchema.CurrentVersion, EntityId.New(), name, target, baseLevel, DefaultContentPath),
            new(ProjectSchema.CurrentVersion, entities.ToArray(), []));
        workspace.Validate();
        await workspace.SaveAsync(cancellationToken);
        return workspace;
    }

    public static async Task<ForgeProjectWorkspace> OpenAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(rootPath);
        var manifestBytes = await File.ReadAllBytesAsync(Path.Combine(root, ManifestFileName), cancellationToken);
        using (ProjectSchema.Parse(manifestBytes)) { }
        var manifest = Deserialize<ForgeProjectManifest>(manifestBytes, "Project manifest");
        var contentPath = ResolveRelativePath(root, manifest.Content);
        var contentBytes = await File.ReadAllBytesAsync(contentPath, cancellationToken);
        using (ProjectSchema.Parse(contentBytes)) { }
        var content = Deserialize<ForgeProjectContent>(contentBytes, "Project content");
        var workspace = new ForgeProjectWorkspace(root, manifest, content);
        workspace.Validate();
        return workspace;
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        Validate();
        await WriteJsonAsync(ResolveRelativePath(RootPath, Manifest.Content), Content, cancellationToken);
        await WriteJsonAsync(Path.Combine(RootPath, ManifestFileName), Manifest, cancellationToken);
    }

    public void UpdateTransform(EntityId entityId, ProjectTransform transform)
    {
        ValidateTransform(transform);
        var index = FindEntityIndex(entityId);
        var entities = Content.Entities.ToArray();
        entities[index] = entities[index] with { Transform = transform };
        Content = Content with { Entities = entities };
    }

    public ProjectEntity RemoveEntity(EntityId entityId)
    {
        var index = FindEntityIndex(entityId);
        var removed = Content.Entities[index];
        Content = Content with { Entities = Content.Entities.Where(entity => entity.EntityId != entityId).ToArray() };
        return removed;
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

    private void Validate()
    {
        if (Manifest.SchemaVersion != ProjectSchema.CurrentVersion) throw new UnsupportedProjectSchemaException(Manifest.SchemaVersion);
        if (Content.SchemaVersion != ProjectSchema.CurrentVersion) throw new UnsupportedProjectSchemaException(Content.SchemaVersion);
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
        ResolveRelativePath(RootPath, Manifest.Content);

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

    private static string ResolveRelativePath(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || relativePath.Contains('\\'))
            throw new InvalidDataException("Project content path must be a portable relative path.");
        var segments = relativePath.Split('/');
        if (segments.Any(segment => segment is "" or "." or ".."))
            throw new InvalidDataException("Project content path contains an invalid segment.");
        var resolved = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
        if (!resolved.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Project content path escapes the project root.");
        return resolved;
    }

    private static void ValidateText(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256)
            throw new InvalidDataException($"{name} must contain between 1 and 256 characters.");
    }

    private static T Deserialize<T>(byte[] bytes, string description) =>
        JsonSerializer.Deserialize<T>(bytes, JsonOptions)
        ?? throw new InvalidDataException($"{description} is empty.");

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions).Append((byte)'\n').ToArray();
        var temporary = $"{path}.{Guid.NewGuid():N}.partial";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
