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
            NativeTerrainGenerationVersion = StaticModelTfragConverter.GetNativeTerrainGenerationVersion(GameId.UYA),
            Sources = workspace.Content.Tfrags?.Sources.Select(value => new
            {
                value.SourceKind,
                value.SourceIndex,
                value.GroupCount,
            }).ToArray(),
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
            CustomImports = workspace.Content.Tfrags?.CustomImports.Select(value => new
            {
                value.ImportId,
                Textures = value.Textures.Select(texture => texture.Id).ToArray(),
                value.Pieces,
            }).ToArray(),
        });

    internal static IEnumerable<AssetId> AssetIds(ForgeProjectWorkspace workspace) =>
        EditedEntities(workspace).Select(value => workspace.ResolveAssetReference(value.Asset!).Id)
            .Concat(workspace.Content.Tfrags?.CustomImports.SelectMany(value => value.Textures)
                .Select(value => value.Id) ?? []);

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
        CancellationToken cancellationToken) => await ComposeCoreAsync(
            workspace, catalog, record, applyEntityTransforms: true, cancellationToken);

    internal static async Task<IReadOnlyDictionary<string, byte[]>> ComposePreviewAsync(
        ForgeProjectWorkspace workspace,
        AssetCatalogStore catalog,
        UyaBaseLayerRecord record,
        CancellationToken cancellationToken) => await ComposeCoreAsync(
            workspace, catalog, record, applyEntityTransforms: false, cancellationToken);

    private static async Task<IReadOnlyDictionary<string, byte[]>> ComposeCoreAsync(
        ForgeProjectWorkspace workspace,
        AssetCatalogStore catalog,
        UyaBaseLayerRecord record,
        bool applyEntityTransforms,
        CancellationToken cancellationToken)
    {
        if (workspace.Content.Tfrags is null)
            throw new InvalidDataException("Project tfrag inventory is unavailable.");
        var entities = Entities(workspace).GroupBy(value =>
            (value.Tfrag!.SourceKind, value.Tfrag.SourceIndex)).ToDictionary(value => value.Key);
        var customIds = workspace.Content.Tfrags.CustomImports
            .SelectMany(value => value.Pieces).ToHashSet();
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
            var baseEntities = sourceEntities.Where(value => !customIds.Contains(value.EntityId)).ToArray();
            var customEntities = sourceEntities.Where(value => customIds.Contains(value.EntityId)).ToArray();
            var path = workspace.ResolveAssetPath(asset.Asset, catalog)
                ?? throw new FileNotFoundException($"Tfrag source {asset.Asset.Id} is missing.");
            var sourceBytes = await File.ReadAllBytesAsync(path, cancellationToken);
            var sourceGroupCount = TfragTerrainReader.Read(sourceBytes).TfragCount;
            if (baseEntities.Length != 0 && baseEntities.Length != sourceGroupCount)
                throw new InvalidDataException(
                    $"Tfrag source {asset.Name} supports retaining all or deleting all vanilla groups.");
            var removedVanilla = sourceGroupCount > 0 && baseEntities.Length == 0;
            var edits = new List<TfragPieceCompositionEdit>();
            foreach (var entity in baseEntities.Where(entity => applyEntityTransforms && TransformChanged(entity)))
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
            if (!removedVanilla && edits.Count == 0 && customEntities.Length == 0) continue;
            var bytes = removedVanilla
                ? customEntities.Length == 0
                    ? TfragTerrainComposer.FilterTerrainGroups(GameId.UYA, sourceBytes, [], cancellationToken)
                    : []
                : edits.Count == 0
                    ? sourceBytes
                : (await Task.Run(
                    () => TfragPieceConverter.Compose(sourceBytes, GameId.UYA, edits, cancellationToken),
                    cancellationToken)).Bytes;
            if (customEntities.Length > 0)
            {
                var pieces = new List<StaticModelTfragPiece>(customEntities.Length);
                foreach (var entity in customEntities)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var transform = applyEntityTransforms
                        ? entity.Transform
                        : ProjectTransform.Identity with { Position = entity.Tfrag!.SourcePosition! };
                    pieces.Add(await LoadCustomPieceAsync(
                        workspace, entity, transform, cancellationToken));
                }
                bytes = await Task.Run(() => removedVanilla
                        ? StaticModelTfragConverter.BuildTerrain(GameId.UYA, pieces, cancellationToken)
                        : TfragTerrainComposer.AppendTerrain(
                            GameId.UYA, bytes, pieces, cancellationToken),
                    cancellationToken);
            }
            result.Add(asset.Name, bytes);
        }
        return result;
    }

    internal static async Task<StaticModelTfragPiece> LoadCustomPieceAsync(
        ForgeProjectWorkspace workspace,
        ProjectEntity entity,
        ProjectTransform transform,
        CancellationToken cancellationToken)
    {
        if (entity.Tfrag?.CustomImportId is null)
            throw new InvalidDataException("A native tfrag cannot be loaded as a custom terrain piece.");
        var canonical = await workspace.ReadAttachedAssetVerifiedAsync(
            entity.Asset!, ForgeProjectWorkspace.MaxAttachedAssetBytes, cancellationToken);
        var geometry = TfragPieceCanonicalCodec.Read(canonical);
        return new StaticModelTfragPiece(
            entity.Tfrag.GroupIndex,
            new(
                TfragPieceCanonicalCodec.ComputeContentId(canonical),
                TfragPieceCanonicalCodec.FormatVersion,
                canonical,
                geometry),
            new(
                Vector(transform.Position),
                new(transform.Rotation.X, transform.Rotation.Y,
                    transform.Rotation.Z, transform.Rotation.W),
                Vector(transform.Scale)),
            [])
        {
            StreamingTextureIds = geometry.TextureIds,
        };
    }

    private static ProjectEntity[] Entities(ForgeProjectWorkspace workspace) =>
        workspace.Content.Entities.Where(value => value.Tfrag is not null)
            .OrderBy(value => value.Tfrag!.SourceKind)
            .ThenBy(value => value.Tfrag!.SourceIndex)
            .ThenBy(value => value.Tfrag!.GroupIndex)
            .ToArray();

    private static IEnumerable<ProjectEntity> EditedEntities(ForgeProjectWorkspace workspace) =>
        workspace.Content.Entities.Where(value => value.Tfrag is not null
                && (value.Tfrag.CustomImportId is not null
                    || value.Tfrag.SourcePosition is null || TransformChanged(value)))
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
