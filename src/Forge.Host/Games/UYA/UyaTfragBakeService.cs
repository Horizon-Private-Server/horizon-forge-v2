using Forge.Host.Domain;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.Tfrags;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.UYA;

internal static class UyaTfragBakeService
{
    internal static byte[] CreateFingerprintSettings(ForgeProjectWorkspace workspace) =>
        ForgeProjectPersistence.Serialize(new
        {
            CompositionVersion = TfragPieceConverter.CompositionVersion,
            Pieces = EditedEntities(workspace).Select(value => new
            {
                value.EntityId,
                AssetId = workspace.ResolveAssetReference(value.Asset!).Id,
                value.Tfrag!.SourceKind,
                value.Tfrag.SourceIndex,
                value.Tfrag.GroupIndex,
                value.Tfrag.SourcePosition,
                value.Transform,
            }).ToArray(),
        });

    internal static IEnumerable<AssetId> AssetIds(ForgeProjectWorkspace workspace) =>
        EditedEntities(workspace).Select(value => workspace.ResolveAssetReference(value.Asset!).Id);

    internal static async Task ValidateEditedAssetsAsync(
        ForgeProjectWorkspace workspace,
        CancellationToken cancellationToken)
    {
        foreach (var entity in EditedEntities(workspace))
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = await workspace.ReadAttachedAssetVerifiedAsync(
                entity.Asset!, ForgeProjectWorkspace.MaxAttachedAssetBytes, cancellationToken);
        }
    }

    internal static async Task<IReadOnlyDictionary<string, byte[]>> ComposeAsync(
        ForgeProjectWorkspace workspace,
        AssetCatalogStore catalog,
        UyaBaseLayerRecord record,
        CancellationToken cancellationToken)
    {
        if (workspace.Content.Tfrags is null)
            throw new InvalidDataException("Project tfrag inventory is unavailable.");
        var entities = Entities(workspace).GroupBy(value =>
            (value.Tfrag!.SourceKind, value.Tfrag.SourceIndex)).ToDictionary(value => value.Key);
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var asset in record.Assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceIndex = UyaBaseLayerStore.TfragSourceIndex(asset.Name);
            var sourceKind = sourceIndex == 0 ? ProjectTfragSourceKind.Primary : ProjectTfragSourceKind.Chunk;
            var sourceState = workspace.Content.Tfrags.Sources.SingleOrDefault(value =>
                value.SourceKind == sourceKind && value.SourceIndex == sourceIndex)
                ?? throw new InvalidDataException($"Tfrag source {asset.Name} has no project inventory.");
            var sourceEntities = entities.GetValueOrDefault((sourceKind, sourceIndex))?
                .OrderBy(value => value.Tfrag!.GroupIndex).ToArray() ?? [];
            if (sourceEntities.Length != sourceState.GroupCount
                || !sourceEntities.Select(value => value.Tfrag!.GroupIndex)
                    .SequenceEqual(Enumerable.Range(0, sourceState.GroupCount)))
                throw new InvalidDataException($"Tfrag source {asset.Name} has an incomplete project inventory.");
            var edits = new List<TfragPieceCompositionEdit>();
            foreach (var entity in sourceEntities.Where(TransformChanged))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var canonical = await workspace.ReadAttachedAssetVerifiedAsync(
                    entity.Asset!, ForgeProjectWorkspace.MaxAttachedAssetBytes, cancellationToken);
                edits.Add(new(
                    entity.EntityId.ToString(),
                    entity.Tfrag!.GroupIndex,
                    canonical,
                    new(
                        Vector(entity.Transform.Position),
                        new(entity.Transform.Rotation.X, entity.Transform.Rotation.Y,
                            entity.Transform.Rotation.Z, entity.Transform.Rotation.W),
                        Vector(entity.Transform.Scale))));
            }
            if (edits.Count == 0) continue;
            var path = workspace.ResolveAssetPath(asset.Asset, catalog)
                ?? throw new FileNotFoundException($"Tfrag source {asset.Asset.Id} is missing.");
            var sourceBytes = await File.ReadAllBytesAsync(path, cancellationToken);
            var composition = await Task.Run(
                () => TfragPieceConverter.Compose(sourceBytes, GameId.UYA, edits, cancellationToken),
                cancellationToken);
            result.Add(asset.Name, composition.Bytes);
        }
        return result;
    }

    private static ProjectEntity[] Entities(ForgeProjectWorkspace workspace) =>
        workspace.Content.Entities.Where(value => value.Tfrag is not null)
            .OrderBy(value => value.Tfrag!.SourceKind)
            .ThenBy(value => value.Tfrag!.SourceIndex)
            .ThenBy(value => value.Tfrag!.GroupIndex)
            .ToArray();

    private static IEnumerable<ProjectEntity> EditedEntities(ForgeProjectWorkspace workspace) =>
        workspace.Content.Entities.Where(value => value.Tfrag is not null
                && (value.Tfrag.SourcePosition is null || TransformChanged(value)))
            .OrderBy(value => value.Tfrag!.SourceKind)
            .ThenBy(value => value.Tfrag!.SourceIndex)
            .ThenBy(value => value.Tfrag!.GroupIndex);

    private static bool TransformChanged(ProjectEntity entity)
    {
        var source = entity.Tfrag!.SourcePosition
            ?? throw new InvalidDataException($"Tfrag {entity.EntityId} has no immutable source position.");
        var rotation = entity.Transform.Rotation;
        var length = MathF.Sqrt(rotation.X * rotation.X + rotation.Y * rotation.Y
            + rotation.Z * rotation.Z + rotation.W * rotation.W);
        var identityRotation = length > 0 && float.IsFinite(length)
            && MathF.Abs(MathF.Abs(rotation.W / length) - 1) <= 0.00001f;
        return entity.Transform.Position != source
            || !identityRotation
            || entity.Transform.Scale != ProjectTransform.Identity.Scale;
    }

    private static TfragPieceVector3 Vector(ProjectVector3 value) => new(value.X, value.Y, value.Z);

}
