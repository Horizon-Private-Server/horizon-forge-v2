namespace Forge.Host.Domain;

public sealed partial class ForgeProjectWorkspace
{
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
