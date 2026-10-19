using System.Buffers.Binary;
using Forge.Host.Domain;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.Gltf;
using RatchetPs2.Core.Ties;
using RatchetPs2.Games.UYA.Gameplay;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.UYA;

internal static class UyaCustomTieProjectService
{
    public static Task<ProjectCustomModel> ImportAsync(
        ForgeProjectWorkspace workspace,
        ProjectCustomModelImportRequest request,
        CancellationToken cancellationToken = default) => ImportCoreAsync(
            workspace, request, null, [], cancellationToken);

    public static Task<ProjectCustomModel> ReimportAsync(
        ForgeProjectWorkspace workspace,
        AssetId previousAssetId,
        ProjectCustomModelImportRequest request,
        IReadOnlyList<EntityId> explicitDependents,
        CancellationToken cancellationToken = default) => ImportCoreAsync(
            workspace, request, previousAssetId, explicitDependents, cancellationToken);

    private static async Task<ProjectCustomModel> ImportCoreAsync(
        ForgeProjectWorkspace workspace,
        ProjectCustomModelImportRequest request,
        AssetId? previousAssetId,
        IReadOnlyList<EntityId> explicitDependents,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Dependencies);
        ArgumentNullException.ThrowIfNull(explicitDependents);
        if (!string.Equals(workspace.Manifest.Target.Game, "UYA", StringComparison.Ordinal))
            throw new NotSupportedException("Custom TIE import requires a UYA project.");
        cancellationToken.ThrowIfCancellationRequested();
        var files = request.Dependencies.Select(file =>
        {
            ArgumentNullException.ThrowIfNull(file);
            ArgumentNullException.ThrowIfNull(file.Bytes);
            return new StaticModelImportFile(file.Uri, file.Bytes);
        }).ToArray();
        var imported = StaticModelGltfImporter.Import(
            request.SourceBytes, request.SourceName, files, () => cancellationToken.IsCancellationRequested);
        cancellationToken.ThrowIfCancellationRequested();
        var canonicalAsset = request.ForceBackfaceCulling
            ? imported.Asset with
            {
                Materials = imported.Asset.Materials
                    .Select(material => material with { DoubleSided = false })
                    .ToArray(),
            }
            : imported.Asset;
        var canonicalBytes = request.ForceBackfaceCulling
            ? StaticModelCanonicalCodec.Write(canonicalAsset)
            : imported.CanonicalBytes;

        var canonicalId = AssetId.Compute(
            AssetKind.Tie, ProjectCustomModelSchema.CanonicalFormatVersion, canonicalBytes);
        var canonical = new ProjectAssetReference(canonicalId, AssetKind.Tie);
        var customAssetIds = workspace.Content.CustomClasses.Allocations
            .Select(value => value.Asset.Id)
            .ToHashSet();
        var occupied = workspace.Content.Entities
            .Where(entity => entity.Asset?.Kind == AssetKind.Tie
                && !customAssetIds.Contains(entity.Asset.Id))
            .Select(entity => entity.Source?.ClassId)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToHashSet();
        var registry = UyaCustomTieClassRegistry.AllocateImported(
            workspace.Content.CustomClasses, canonicalId, occupied);
        var classId = registry.Allocations.Single(value => value.Asset == canonical).ClassId;
        var converted = StaticModelConverter.Convert(GameId.UYA, canonicalAsset, classId, cancellationToken);

        var attachments = new ProjectAssetAttachmentCollector();
        var (source, dependencies) = attachments.AttachStaticModelSources(
            request, imported.Provenance, ProjectCustomModelSchema.SourceFormatVersion);
        _ = attachments.Attach(AssetKind.Tie, ProjectCustomModelSchema.CanonicalFormatVersion, canonicalBytes);

        var textureByConverterId = new Dictionary<string, ProjectAssetReference>(StringComparer.Ordinal);
        foreach (var texture in converted.Textures)
        {
            var reference = attachments.Attach(
                AssetKind.Texture, ProjectTextureAssetSchema.CanonicalFormatVersion, texture.PifBytes);
            textureByConverterId.Add(texture.AssetId, reference);
        }
        var textures = converted.Textures.Select(texture => textureByConverterId[texture.AssetId])
            .Distinct().ToArray();
        var nativeBytes = UyaCustomTieNativeCodec.Write(converted.DefinitionBytes, converted.ModelBytes);
        var native = attachments.Attach(
            AssetKind.StaticModelNative,
            ProjectCustomModelSchema.NativeFormatVersion,
            nativeBytes,
            canonicalId);
        var model = new ProjectCustomModel(
            ProjectCustomModelSchema.CurrentVersion,
            imported.Provenance.SourceName,
            imported.Provenance.SourceSha256,
            source,
            dependencies,
            canonical,
            native,
            textures,
            converted.Materials.Select(material => new ProjectCustomModelMaterialBinding(
                material.NativeMaterialIndex,
                material.SourceMaterialIndex,
                textureByConverterId[material.TextureAssetId],
                material.ClampU,
                material.ClampV)).ToArray(),
            new(
                canonicalAsset.Primitives.Sum(primitive => primitive.Positions.Length / 3),
                canonicalAsset.Primitives.Sum(primitive => primitive.Indices.Length / 3),
                converted.Primitives.Sum(primitive => primitive.PacketCount),
                converted.Materials.Count,
                converted.Textures.Count,
                converted.Textures.Select(texture => texture.PaletteIndex).Distinct().Count(),
                new(
                    canonicalAsset.Bounds.MinimumX,
                    canonicalAsset.Bounds.MinimumY,
                    canonicalAsset.Bounds.MinimumZ,
                    canonicalAsset.Bounds.MaximumX,
                    canonicalAsset.Bounds.MaximumY,
                    canonicalAsset.Bounds.MaximumZ),
                converted.LodPolicy,
                converted.Primitives.Select(primitive => new ProjectCustomModelPrimitiveBinding(
                    primitive.SourceMeshIndex,
                    primitive.SourcePrimitiveIndex,
                    primitive.NativeMaterialIndex,
                    primitive.FirstTriangle,
                    primitive.TriangleCount,
                    primitive.FirstPacket,
                    primitive.PacketCount)).ToArray(),
                imported.Diagnostics.Concat(converted.Diagnostics).Select(FormatDiagnostic).ToArray()));

        var models = workspace.Content.CustomModels.Models.ToList();
        var existingIndex = models.FindIndex(value => value.Canonical == canonical);
        var replacingExisting = existingIndex >= 0;
        if (existingIndex < 0) models.Add(model);
        else models[existingIndex] = model;
        var entities = workspace.Content.Entities.ToArray();
        var ambientSize = TieClassReader.Read(converted.ModelBytes).Header.AmbientSize;
        if (previousAssetId is { } previous)
        {
            if (!models.Any(value => value.Canonical.Id == previous))
                throw new KeyNotFoundException($"Custom model {previous} is not present in the project.");
            var explicitIds = explicitDependents.ToHashSet();
            if (explicitIds.Count != explicitDependents.Count
                || entities.Where(entity => explicitIds.Contains(entity.EntityId))
                    .Any(entity => entity.Asset is not { Kind: AssetKind.Tie } asset || asset.Id != previous))
                throw new InvalidOperationException("Reimport dependents must currently reference the previous custom model.");
            var lightingSources = entities;
            entities = entities.Select(entity => explicitIds.Contains(entity.EntityId)
                ? Retarget(entity, canonical, classId, ambientSize, lightingSources)
                : entity).ToArray();
            if (previous != canonicalId && !entities.Any(entity => entity.Asset?.Id == previous))
                models.RemoveAll(value => value.Canonical.Id == previous);
        }
        else if (replacingExisting)
        {
            var lightingSources = entities;
            entities = entities.Select(entity => entity.Asset == canonical
                ? Retarget(entity, canonical, classId, ambientSize, lightingSources)
                : entity).ToArray();
        }
        var state = new ProjectCustomModelState(
            ProjectCustomModelSchema.CurrentVersion,
            models.OrderBy(value => value.Canonical.Id.ToString(), StringComparer.Ordinal).ToArray());
        await workspace.CommitCustomModelsAsync(
            state, registry, entities, attachments.Attachments, cancellationToken);
        return model;
    }

    private static ProjectEntity Retarget(
        ProjectEntity entity,
        ProjectAssetReference asset,
        int classId,
        int ambientSize,
        IReadOnlyList<ProjectEntity> lightingSources)
    {
        if (entity.Source is null || entity.Source.RawRecord.Length != UyaTieInstancesReader.RecordSize)
            throw new InvalidDataException("A custom TIE dependent has no valid native instance record.");
        var rawRecord = entity.Source.RawRecord.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(rawRecord, classId);
        var neutral = UyaGameplayInstanceTemplates.CreateNeutralTieAmbient(ambientSize);
        var lighting = entity.TieLighting is { } previous
                && previous.AmbientRgbas.Length == ambientSize
                && !previous.AmbientRgbas.SequenceEqual(neutral)
            ? previous
            : UyaTieLightingDefaults.Create(lightingSources, entity.Transform.Position, ambientSize);
        return entity with
        {
            Asset = asset,
            Source = entity.Source with { ClassId = classId, RawRecord = rawRecord },
            TieLighting = lighting,
        };
    }

    internal static Task DeleteAsync(
        ForgeProjectWorkspace workspace,
        AssetId modelAssetId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        workspace.DeleteCustomModel(modelAssetId);
        return Task.CompletedTask;
    }

    private static string FormatDiagnostic(StaticModelNativeDiagnostic diagnostic) =>
        string.Join(':', new string?[]
        {
            diagnostic.Code,
            diagnostic.SourceMeshIndex?.ToString(),
            diagnostic.SourcePrimitiveIndex?.ToString(),
            diagnostic.SourceMaterialIndex?.ToString(),
            diagnostic.SourceTextureIndex?.ToString(),
            diagnostic.Message,
        }.Where(value => value is not null));
}
