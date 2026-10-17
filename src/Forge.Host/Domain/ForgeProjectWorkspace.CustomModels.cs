namespace Forge.Host.Domain;

public sealed partial class ForgeProjectWorkspace
{
    internal async Task CommitCustomModelsAsync(
        ProjectCustomModelState customModels,
        ProjectCustomClassRegistry customClasses,
        IReadOnlyList<ProjectEntity> entities,
        IReadOnlyList<ProjectAssetAttachment> attachments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(customModels);
        ArgumentNullException.ThrowIfNull(customClasses);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(attachments);
        cancellationToken.ThrowIfCancellationRequested();

        var resolved = attachments.Select(attachment =>
        {
            ArgumentNullException.ThrowIfNull(attachment);
            return (Asset: CreateAttachedAsset(
                    attachment.Kind,
                    attachment.CanonicalFormatVersion,
                    attachment.CanonicalBytes,
                    attachment.ParentId,
                    nameof(attachments),
                    nameof(attachments)),
                Bytes: attachment.CanonicalBytes);
        }).ToArray();
        var unique = new List<(ProjectAttachedAsset Asset, ReadOnlyMemory<byte> Bytes)>();
        foreach (var group in resolved.GroupBy(value => value.Asset.Id))
        {
            var first = group.First();
            if (group.Any(value => value.Asset != first.Asset
                    || !value.Bytes.Span.SequenceEqual(first.Bytes.Span)))
                throw new InvalidDataException($"Project asset {group.Key} has inconsistent transaction data.");
            unique.Add(first);
        }

        var existing = Content.Assets.ToDictionary(asset => asset.Id);
        foreach (var value in unique)
            if (existing.TryGetValue(value.Asset.Id, out var current) && current != value.Asset)
                throw new InvalidDataException($"Project asset {value.Asset.Id} has inconsistent metadata.");
        var added = unique.Where(value => !existing.ContainsKey(value.Asset.Id)).Select(value => value.Asset);
        var content = Content with
        {
            Assets = Content.Assets.Concat(added)
                .OrderBy(asset => asset.Id.ToString(), StringComparer.Ordinal).ToArray(),
            Entities = entities.ToArray(),
            CustomClasses = customClasses,
            CustomModels = customModels,
        };
        ForgeProjectValidation.Validate(RootPath, Manifest, content);

        var created = new List<string>();
        try
        {
            foreach (var value in unique)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = AssetBlobPath(value.Asset.Id);
                var existed = File.Exists(path);
                await EnsureAssetBlobAsync(value.Asset, value.Bytes, cancellationToken);
                if (!existed) created.Add(path);
            }
            cancellationToken.ThrowIfCancellationRequested();
            Content = content;
        }
        catch
        {
            foreach (var path in created) File.Delete(path);
            throw;
        }
    }

    public ProjectCustomModelDeleteReport InspectCustomModelDelete(AssetId modelAssetId)
    {
        var model = Content.CustomModels.Models.SingleOrDefault(value => value.Canonical.Id == modelAssetId)
            ?? throw new KeyNotFoundException($"Custom model {modelAssetId} is not present in the project.");
        var instances = Content.Entities.Where(entity => entity.Asset?.Id == modelAssetId)
            .Select(entity => entity.EntityId).ToArray();
        var owned = model.Dependencies.Select(value => value.Asset.Id)
            .Prepend(model.Source.Id)
            .Concat(model.Textures.Select(value => value.Id))
            .Append(model.Native.Id)
            .Distinct().OrderBy(value => value.ToString(), StringComparer.Ordinal).ToArray();
        var dependents = Content.AssetOverrides
            .Where(binding => binding.Source.Id == modelAssetId || binding.Replacement.Id == modelAssetId)
            .Select(binding => $"asset override {binding.Source.Id}")
            .Concat(Content.InstancedCollisionBindings
                .Where(binding => binding.SourceAssetId == modelAssetId || binding.ProxyAssetId == modelAssetId)
                .Select(binding => $"collision proxy {binding.ProxyAssetId}"))
            .Order(StringComparer.Ordinal).ToArray();
        return new(modelAssetId, instances, owned, dependents);
    }

    internal void DeleteCustomModel(AssetId modelAssetId)
    {
        var report = InspectCustomModelDelete(modelAssetId);
        if (report.IsBlocked) throw new ProjectCustomModelDeleteBlockedException(report);
        Content = Content with
        {
            CustomModels = Content.CustomModels with
            {
                Models = Content.CustomModels.Models
                    .Where(model => model.Canonical.Id != modelAssetId).ToArray(),
            },
        };
        Validate();
    }
}
