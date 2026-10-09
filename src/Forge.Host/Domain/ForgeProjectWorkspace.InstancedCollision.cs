namespace Forge.Host.Domain;

public sealed partial class ForgeProjectWorkspace
{
    public Task<ProjectInstancedCollisionBinding> ApplyInstancedCollisionProxyAsync(
        AssetId sourceAssetId,
        ReadOnlyMemory<byte> canonicalBytes,
        uint canonicalFormatVersion,
        ProjectInstancedCollisionRecipe recipe,
        CancellationToken cancellationToken = default)
        => ApplyInstancedCollisionProxyCoreAsync(
            sourceAssetId, null, canonicalBytes, canonicalFormatVersion, recipe, cancellationToken);

    public async Task<ProjectInstancedCollisionBinding> ApplyInstancedCollisionProxyAsync(
        EntityId instanceEntityId,
        ReadOnlyMemory<byte> canonicalBytes,
        uint canonicalFormatVersion,
        ProjectInstancedCollisionRecipe recipe,
        CancellationToken cancellationToken = default)
    {
        var entity = GetEntities([instanceEntityId])[0];
        var sourceAssetId = entity.Asset is { } asset && asset.IsInstancedCollisionSource()
            ? asset.Id
            : throw new InvalidOperationException("Individual collision requires a TIE or shrub entity.");
        return await ApplyInstancedCollisionProxyCoreAsync(
            sourceAssetId, instanceEntityId, canonicalBytes, canonicalFormatVersion, recipe, cancellationToken);
    }

    private async Task<ProjectInstancedCollisionBinding> ApplyInstancedCollisionProxyCoreAsync(
        AssetId sourceAssetId,
        EntityId? instanceEntityId,
        ReadOnlyMemory<byte> canonicalBytes,
        uint canonicalFormatVersion,
        ProjectInstancedCollisionRecipe recipe,
        CancellationToken cancellationToken)
    {
        if (sourceAssetId.ToString().Length != AssetId.TextLength)
            throw new ArgumentException("Source Asset ID is invalid.", nameof(sourceAssetId));
        if (canonicalBytes.IsEmpty)
            throw new ArgumentException("Collision proxy bytes cannot be empty.", nameof(canonicalBytes));
        ForgeProjectValidation.ValidateInstancedCollisionRecipe(recipe);
        if (!Content.Entities.Any(entity => entity.Asset is { } asset && asset.IsInstancedCollisionSource()
            && asset.Id == sourceAssetId))
            throw new InvalidOperationException($"Project has no supported instanced entity for asset {sourceAssetId}.");

        var attached = await AttachAssetAsync(
            AssetKind.Collision, canonicalFormatVersion, canonicalBytes, sourceAssetId, cancellationToken);
        var proxyAssetId = attached.Id;
        var binding = new ProjectInstancedCollisionBinding(sourceAssetId, proxyAssetId, recipe, [], instanceEntityId);
        var content = Content with
        {
            InstancedCollisionBindings = Content.InstancedCollisionBindings
                .Where(value => value.SourceAssetId != sourceAssetId || value.InstanceEntityId != instanceEntityId)
                .Append(binding)
                .OrderBy(value => value.SourceAssetId.ToString(), StringComparer.Ordinal)
                .ThenBy(value => value.InstanceEntityId?.ToString(), StringComparer.Ordinal)
                .ToArray(),
        };
        ForgeProjectValidation.Validate(RootPath, Manifest, content);
        Content = content;
        return binding;
    }

    public ProjectInstancedCollisionBinding RemoveInstancedCollisionProxy(AssetId sourceAssetId)
    {
        var binding = Content.InstancedCollisionBindings.SingleOrDefault(value =>
                value.SourceAssetId == sourceAssetId && value.InstanceEntityId is null)
            ?? throw new KeyNotFoundException($"Source asset {sourceAssetId} has no collision proxy binding.");
        var content = Content with
        {
            InstancedCollisionBindings = Content.InstancedCollisionBindings.Where(value => value != binding).ToArray(),
            Entities = Content.Entities.Select(entity => entity.Asset?.Id == sourceAssetId
                ? entity with
                {
                    InstancedCollisionEnabled = entity.InstancedCollisionEnabled == true
                        ? false
                        : entity.InstancedCollisionEnabled,
                }
                : entity).ToArray(),
        };
        ForgeProjectValidation.Validate(RootPath, Manifest, content);
        Content = content;
        return binding;
    }

    public ProjectInstancedCollisionBinding RemoveInstancedCollisionProxy(EntityId entityId)
    {
        var entity = GetEntities([entityId])[0];
        if (!entity.Asset.IsInstancedCollisionSource())
            throw new InvalidOperationException("Individual collision requires a TIE or shrub entity.");
        if (entity.InstancedCollisionEnabled != false) return RemoveInstancedCollisionProxy(entity.Asset.Id);
        var binding = Content.InstancedCollisionBindings.SingleOrDefault(value => value.InstanceEntityId == entityId)
            ?? throw new KeyNotFoundException($"Entity {entityId} has no individual collision proxy binding.");
        var content = Content with
        {
            InstancedCollisionBindings = Content.InstancedCollisionBindings.Where(value => value != binding).ToArray(),
        };
        ForgeProjectValidation.Validate(RootPath, Manifest, content);
        Content = content;
        return binding;
    }

    public void SetInstancedCollisionEnabled(EntityId entityId, bool? enabled)
        => SetInstancedCollisionEnabled([entityId], enabled);

    public void SetInstancedCollisionEnabled(IReadOnlyList<EntityId> entityIds, bool? enabled)
    {
        var entities = GetEntities(entityIds);
        if (entities.Any(entity => !entity.Asset.IsInstancedCollisionSource()))
            throw new InvalidOperationException("Only TIE and shrub entities can override generated collision.");
        UpdateEntities(entityIds, value => value with { InstancedCollisionEnabled = enabled });
    }

    public void SetInstancedCollisionRawType(AssetId sourceAssetId, byte rawType)
    {
        var binding = Content.InstancedCollisionBindings.SingleOrDefault(value =>
                value.SourceAssetId == sourceAssetId && value.InstanceEntityId is null)
            ?? throw new KeyNotFoundException($"Source asset {sourceAssetId} has no collision proxy binding.");
        SetInstancedCollisionRawType(binding, rawType);
    }

    public void SetInstancedCollisionRawType(EntityId entityId, byte rawType) =>
        SetInstancedCollisionRawType(GetInstancedCollisionBinding(entityId), rawType);

    private void SetInstancedCollisionRawType(ProjectInstancedCollisionBinding binding, byte rawType)
    {
        var replacement = binding with
        {
            Recipe = binding.Recipe with { RawType = rawType },
            FaceTypeOverrides = binding.FaceTypeOverrides.Where(value => value.RawType != rawType).ToArray(),
        };
        var content = Content with
        {
            InstancedCollisionBindings = Content.InstancedCollisionBindings.Select(value =>
                value == binding ? replacement : value).ToArray(),
        };
        ForgeProjectValidation.Validate(RootPath, Manifest, content);
        Content = content;
    }

    public void SetInstancedCollisionFaceTypes(
        AssetId sourceAssetId,
        AssetId expectedProxyAssetId,
        IReadOnlyList<ProjectCollisionFaceTypeOverride> assignments,
        int faceCount)
    {
        var binding = Content.InstancedCollisionBindings.SingleOrDefault(value =>
                value.SourceAssetId == sourceAssetId && value.InstanceEntityId is null)
            ?? throw new KeyNotFoundException($"Source asset {sourceAssetId} has no collision proxy binding.");
        SetInstancedCollisionFaceTypes(binding, expectedProxyAssetId, assignments, faceCount);
    }

    public void SetInstancedCollisionFaceTypes(
        EntityId entityId,
        AssetId expectedProxyAssetId,
        IReadOnlyList<ProjectCollisionFaceTypeOverride> assignments,
        int faceCount) =>
        SetInstancedCollisionFaceTypes(GetInstancedCollisionBinding(entityId), expectedProxyAssetId, assignments, faceCount);

    private void SetInstancedCollisionFaceTypes(
        ProjectInstancedCollisionBinding binding,
        AssetId expectedProxyAssetId,
        IReadOnlyList<ProjectCollisionFaceTypeOverride> assignments,
        int faceCount)
    {
        ArgumentNullException.ThrowIfNull(assignments);
        if (faceCount < 1) throw new ArgumentOutOfRangeException(nameof(faceCount));
        if (assignments.Count is 0 or > ForgeProjectValidation.MaxInstancedCollisionFaceTypeOverrides
            || assignments.Any(value => value is null || value.FaceIndex < 0 || value.FaceIndex >= faceCount)
            || assignments.Select(value => value.FaceIndex).Distinct().Count() != assignments.Count)
            throw new ArgumentException("Instanced collision face-type assignments are invalid.", nameof(assignments));
        if (binding.ProxyAssetId != expectedProxyAssetId)
            throw new InvalidOperationException("The instanced collision proxy changed before face types were applied.");

        var overrides = binding.FaceTypeOverrides.ToDictionary(value => value.FaceIndex);
        foreach (var assignment in assignments)
        {
            if (assignment.RawType == binding.Recipe.RawType) overrides.Remove(assignment.FaceIndex);
            else overrides[assignment.FaceIndex] = assignment;
        }
        var replacement = binding with
        {
            FaceTypeOverrides = overrides.Values.OrderBy(value => value.FaceIndex).ToArray(),
        };
        var content = Content with
        {
            InstancedCollisionBindings = Content.InstancedCollisionBindings.Select(value =>
                value == binding ? replacement : value).ToArray(),
        };
        ForgeProjectValidation.Validate(RootPath, Manifest, content);
        Content = content;
    }

    private ProjectInstancedCollisionBinding GetInstancedCollisionBinding(EntityId entityId)
    {
        var entity = GetEntities([entityId])[0];
        var assetId = entity.Asset is { } asset && asset.IsInstancedCollisionSource()
            ? asset.Id
            : throw new InvalidOperationException("Instanced collision requires a TIE or shrub entity.");
        var instanceEntityId = entity.InstancedCollisionEnabled == false ? entityId : (EntityId?)null;
        return Content.InstancedCollisionBindings.SingleOrDefault(value =>
                value.SourceAssetId == assetId && value.InstanceEntityId == instanceEntityId)
            ?? throw new KeyNotFoundException($"Entity {entityId} has no collision proxy binding for its current mode.");
    }
}
