using System.Security.Cryptography;
using System.Text;
using Forge.Host.Domain;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.Gltf;
using RatchetPs2.Core.LevelAssets;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.UYA;

internal static class UyaCustomTfragProjectService
{
    public static async Task<IReadOnlyList<EntityId>> DeleteAsync(
        ForgeProjectWorkspace workspace,
        IReadOnlyList<EntityId> selectedEntityIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(selectedEntityIds);
        if (workspace.Content.Tfrags is null)
            throw new InvalidOperationException("Project tfrag pieces have not been imported.");
        var selected = workspace.GetEntities(selectedEntityIds);
        if (selected.Any(entity => entity.Tfrag is null))
            throw new InvalidOperationException("Terrain deletion can only contain tfrag pieces.");
        var importIds = selected.Select(entity => entity.Tfrag!.CustomImportId)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        var removedImports = workspace.Content.Tfrags.CustomImports
            .Where(value => importIds.Contains(value.ImportId)).ToArray();
        if (removedImports.Length != importIds.Count)
            throw new InvalidDataException("Custom terrain import ownership is inconsistent.");
        var selectedNative = selected.Where(entity => entity.Tfrag!.CustomImportId is null).ToArray();
        foreach (var source in selectedNative.GroupBy(entity => (
                     entity.Tfrag!.SourceKind, entity.Tfrag.SourceIndex, entity.Tfrag.SourceArchive)))
        {
            var available = workspace.Content.Entities.Count(entity => entity.Tfrag is
                { CustomImportId: null } tfrag
                && (tfrag.SourceKind, tfrag.SourceIndex, tfrag.SourceArchive) == source.Key);
            if (source.Count() != available)
                throw new InvalidOperationException(
                    "Delete every vanilla tfrag in the source together; partial native terrain deletion is not supported yet.");
        }
        var removedIds = selectedNative.Select(value => value.EntityId)
            .Concat(removedImports.SelectMany(value => value.Pieces)).ToHashSet();
        var removedEntities = workspace.Content.Entities.Where(entity => removedIds.Contains(entity.EntityId))
            .ToArray();
        if (removedEntities.Length != removedIds.Count)
            throw new InvalidDataException("Custom terrain piece inventory is incomplete.");
        var removedGroups = removedEntities.GroupBy(entity => (
                entity.Tfrag!.SourceKind, entity.Tfrag.SourceIndex, entity.Tfrag.SourceArchive))
            .ToDictionary(group => group.Key, group => group.Select(entity => entity.Tfrag!.GroupIndex)
                .Order().ToArray());
        var entities = workspace.Content.Entities.Where(entity => !removedIds.Contains(entity.EntityId))
            .Select(entity => ShiftAfterRemovedGroups(entity, removedGroups)).ToArray();
        var state = workspace.Content.Tfrags with
        {
            Sources = workspace.Content.Tfrags.Sources.Select(source =>
            {
                var key = (source.SourceKind, source.SourceIndex, source.SourceArchive);
                return removedGroups.TryGetValue(key, out var groups)
                    ? source with { GroupCount = checked(source.GroupCount - groups.Length) }
                    : source;
            }).ToArray(),
            CustomImports = workspace.Content.Tfrags.CustomImports
                .Where(value => !importIds.Contains(value.ImportId)).ToArray(),
        };
        await workspace.CommitTfragStructureAsync(state, entities, cancellationToken);
        return removedIds.OrderBy(value => value.ToString(), StringComparer.Ordinal).ToArray();
    }

    public static async Task<IReadOnlyList<EntityId>> ImportAsync(
        ForgeProjectWorkspace workspace,
        ProjectCustomModelImportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(request);
        if (workspace.Manifest.Target.Game != "UYA" || workspace.Content.Tfrags is null)
            throw new InvalidOperationException("Custom tfrag import requires an initialized UYA terrain project.");
        var files = request.Dependencies.Select(value =>
            new StaticModelImportFile(value.Uri, value.Bytes)).ToArray();
        var imported = StaticModelTfragConverter.Import(
            GameId.UYA, request.SourceBytes, request.SourceName, files, cancellationToken);
        var importId = imported.Model.ContentSha256;
        var existingImports = workspace.Content.Tfrags.CustomImports.Where(value =>
            value.ImportId == importId
            || string.Equals(value.SourceName, request.SourceName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (existingImports.Length > 1)
            throw new InvalidOperationException(
                $"{request.SourceName} matches multiple custom terrain imports and cannot be replaced safely.");
        var existingImport = existingImports.SingleOrDefault();
        var existingEntities = existingImport is null
            ? []
            : existingImport.Pieces.Select(id => workspace.Content.Entities
                    .Single(entity => entity.EntityId == id))
                .OrderBy(entity => entity.Tfrag!.GroupIndex).ToArray();
        var convertedTextures = StaticModelTfragConverter.ConvertTextures(
            GameId.UYA, imported.Model.Asset, cancellationToken);
        StaticModelTfragConverter.ValidateTextureAllocation(
            GameId.UYA, 0, convertedTextures.Textures.Count);

        var attachments = new ProjectAssetAttachmentCollector();
        var (source, dependencies) = attachments.AttachStaticModelSources(
            request, imported.Model.Provenance, ProjectCustomTfragSchema.SourceFormatVersion);
        var textureByConverterId = convertedTextures.Textures
            .GroupBy(value => value.AssetId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var first = group.First();
                    if (group.Skip(1).Any(value => !value.PifBytes.SequenceEqual(first.PifBytes)))
                        throw new InvalidDataException(
                            $"Converted textures have conflicting payloads for content ID {group.Key}.");
                    return attachments.Attach(
                        AssetKind.Texture, ProjectTextureAssetSchema.CanonicalFormatVersion,
                        first.PifBytes);
                },
                StringComparer.Ordinal);
        var textures = textureByConverterId.Values
            .Distinct()
            .OrderBy(value => value.Id.ToString(), StringComparer.Ordinal)
            .ToArray();
        var localAllocations = textures.Select((value, index) =>
            new TerrainTextureAllocation(value.Id.ToString(), index)).ToArray();
        var localConversion = convertedTextures with
        {
            Materials = convertedTextures.Materials.Select(value => value with
            {
                TextureAssetId = textureByConverterId[value.TextureAssetId].Id.ToString(),
            }).ToArray(),
        };
        var pieces = StaticModelTfragConverter.BuildPieces(
            GameId.UYA,
            imported.Model.Asset,
            imported.Partitions,
            localConversion,
            localAllocations,
            cancellationToken);

        var primary = workspace.Content.Tfrags.Sources.Single(value =>
            value.SourceKind == ProjectTfragSourceKind.Primary && value.SourceIndex == 0);
        var groupStart = existingEntities.Length == 0
            ? primary.GroupCount : existingEntities[0].Tfrag!.GroupIndex;
        if (existingEntities.Length > 0 && !existingEntities.Select(entity => entity.Tfrag!.GroupIndex)
                .SequenceEqual(Enumerable.Range(groupStart, existingEntities.Length)))
            throw new InvalidDataException("Existing custom terrain pieces are not stored in one contiguous range.");
        var sharedOffset = existingEntities.FirstOrDefault() is
                { Tfrag.SourcePosition: { } anchorSourcePosition } first
            ? Subtract(first.Transform.Position, anchorSourcePosition)
            : new ProjectVector3(0, 0, 0);
        var entities = new List<ProjectEntity>(pieces.Count);
        for (var index = 0; index < pieces.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var piece = pieces[index];
            var groupIndex = checked(groupStart + index);
            var reference = attachments.Attach(
                AssetKind.Tfrag, ProjectTfragSchema.CanonicalFormatVersion,
                piece.Asset.CanonicalBytes);
            var existingEntity = index < existingEntities.Length ? existingEntities[index] : null;
            var sourcePosition = Vector(piece.Transform.Position);
            var offset = existingEntity?.Tfrag?.SourcePosition is { } previousSourcePosition
                ? Subtract(existingEntity.Transform.Position, previousSourcePosition)
                : sharedOffset;
            var transform = new ProjectTransform(
                Add(sourcePosition, offset),
                existingEntity?.Transform.Rotation ?? Quaternion(piece.Transform.Rotation),
                existingEntity?.Transform.Scale ?? Vector(piece.Transform.Scale));
            var entityId = existingEntity?.EntityId ?? EntityId.New();
            entities.Add(new(
                entityId,
                $"{Path.GetFileNameWithoutExtension(request.SourceName)} #{index}",
                "tfrags",
                transform,
                reference,
                new("UYA", workspace.Manifest.BaseLevel.Level, primary.SourceArchive, groupIndex),
                existingEntity?.State ?? new(),
                Tfrag: new(
                    OccurrenceKey(importId, index),
                    primary.SourceArchive,
                    ProjectTfragSourceKind.Primary,
                    0,
                    groupIndex,
                    -1,
                    byte.MaxValue,
                    piece.Asset.Geometry.TextureIds,
                    sourcePosition,
                    new(
                        Vector(piece.Asset.Geometry.Bounds.Minimum),
                        Vector(piece.Asset.Geometry.Bounds.Maximum),
                        piece.Asset.Geometry.Bounds.Radius),
                    new(
                        piece.Asset.CanonicalBytes.Length,
                        imported.Partitions[index].VertexCount,
                        imported.Partitions[index].TriangleCount,
                        piece.Asset.Geometry.Lods[0].Primitives.Sum(value => value.Vertices.Count),
                        piece.Asset.Geometry.Lods[0].Primitives.Sum(value => value.Indices.Count / 3)),
                    importId,
                    request.SourceName)));
        }

        var customImport = new ProjectCustomTfragImport(
            importId,
            request.SourceName,
            source,
            dependencies,
            textures,
            entities.Select(value => value.EntityId).ToArray());
        var countDelta = entities.Count - existingEntities.Length;
        var state = workspace.Content.Tfrags with
        {
            Sources = workspace.Content.Tfrags.Sources.Select(value => value == primary
                ? value with { GroupCount = checked(value.GroupCount + countDelta) }
                : value).ToArray(),
            CustomImports = workspace.Content.Tfrags.CustomImports
                .Where(value => value != existingImport).Append(customImport)
                .OrderBy(value => value.ImportId, StringComparer.Ordinal).ToArray(),
        };
        var replacementEntities = ReplaceEntities(
            workspace.Content.Entities, existingEntities, entities, countDelta);
        await workspace.CommitCustomTfragsAsync(
            state, replacementEntities, attachments.Attachments, cancellationToken);
        return entities.Select(value => value.EntityId).ToArray();
    }

    private static IReadOnlyList<ProjectEntity> ReplaceEntities(
        IReadOnlyList<ProjectEntity> current,
        IReadOnlyList<ProjectEntity> previous,
        IReadOnlyList<ProjectEntity> replacement,
        int groupIndexDelta)
    {
        if (previous.Count == 0) return current.Concat(replacement).ToArray();
        var previousIds = previous.Select(entity => entity.EntityId).ToHashSet();
        var previousEnd = previous[^1].Tfrag!.GroupIndex;
        var insertionIndex = current.Select((entity, index) => (entity, index))
            .Where(value => previousIds.Contains(value.entity.EntityId))
            .Min(value => value.index);
        var entities = new List<ProjectEntity>(current.Count - previous.Count + replacement.Count);
        for (var index = 0; index < current.Count; index++)
        {
            if (index == insertionIndex) entities.AddRange(replacement);
            var entity = current[index];
            if (previousIds.Contains(entity.EntityId)) continue;
            if (groupIndexDelta != 0 && entity.Tfrag is
                    { SourceKind: ProjectTfragSourceKind.Primary, SourceIndex: 0 } tfrag
                && tfrag.GroupIndex > previousEnd)
            {
                var groupIndex = checked(tfrag.GroupIndex + groupIndexDelta);
                entity = entity with
                {
                    Provenance = entity.Provenance! with { SourceIndex = groupIndex },
                    Tfrag = tfrag with { GroupIndex = groupIndex },
                };
            }
            entities.Add(entity);
        }
        return entities;
    }

    private static ProjectEntity ShiftAfterRemovedGroups(
        ProjectEntity entity,
        IReadOnlyDictionary<(ProjectTfragSourceKind SourceKind, int SourceIndex, string SourceArchive), int[]>
            removedGroups)
    {
        if (entity.Tfrag is not { } tfrag
            || !removedGroups.TryGetValue(
                (tfrag.SourceKind, tfrag.SourceIndex, tfrag.SourceArchive), out var groups))
            return entity;
        var shift = groups.Count(groupIndex => groupIndex < tfrag.GroupIndex);
        if (shift == 0) return entity;
        var groupIndex = checked(tfrag.GroupIndex - shift);
        return entity with
        {
            Provenance = entity.Provenance! with { SourceIndex = groupIndex },
            Tfrag = tfrag with { GroupIndex = groupIndex },
        };
    }

    private static string OccurrenceKey(string importId, int pieceIndex) =>
        Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"custom-tfrag:{importId}:{pieceIndex}"))).ToLowerInvariant();

    private static ProjectVector3 Vector(RatchetPs2.Core.Tfrags.TfragPieceVector3 value) =>
        new(value.X, value.Y, value.Z);

    private static ProjectQuaternion Quaternion(RatchetPs2.Core.Tfrags.TfragPieceVector4 value) =>
        new(value.X, value.Y, value.Z, value.W);

    private static ProjectVector3 Add(ProjectVector3 left, ProjectVector3 right) =>
        new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);

    private static ProjectVector3 Subtract(ProjectVector3 left, ProjectVector3 right) =>
        new(left.X - right.X, left.Y - right.Y, left.Z - right.Z);
}
