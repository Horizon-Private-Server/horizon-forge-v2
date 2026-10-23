namespace Forge.Host.Domain;

public sealed partial class EditorRuntime
{
    private EditorCommandResult CommandResult(
        EditorCommand command,
        IReadOnlyList<EditorTransformUpdate>? transforms)
    {
        var workspace = RequireWorkspace();
        if (command.Kind is EditorCommandKind.UpdateTransform or EditorCommandKind.UpdateTransforms)
        {
            var entities = workspace.GetEntities(transforms!.Select(value => value.EntityId).ToArray());
            var bindings = entities.Any(entity => entity.Asset.IsInstancedCollisionSource())
                ? workspace.Content.InstancedCollisionBindings.ToDictionary(
                    binding => (binding.SourceAssetId, binding.InstanceEntityId))
                : null;
            return new(
                EditorCommandResultKind.Transforms,
                null,
                entities.Select(entity => new EditorTransformPatch(
                    entity.EntityId,
                    entity.Transform,
                    EntityIsDirty(entity, bindings))).ToArray(),
                null,
                workspace.IsDirty,
                _history.CanUndo,
                _history.CanRedo,
                _eventSequence);
        }
        if (command.Kind == EditorCommandKind.UpdateLevelSettings)
            return new(
                EditorCommandResultKind.LevelSettings,
                null,
                [],
                workspace.Content.LevelSettings,
                workspace.IsDirty,
                _history.CanUndo,
                _history.CanRedo,
                _eventSequence);
        return new(
            EditorCommandResultKind.Snapshot,
            Snapshot(),
            [],
            null,
            workspace.IsDirty,
            _history.CanUndo,
            _history.CanRedo,
            _eventSequence);
    }

    private EditorSnapshot Snapshot()
    {
        var workspace = RequireWorkspace();
        var selection = _selection.ToHashSet();
        var bindings = workspace.Content.InstancedCollisionBindings.ToDictionary(
            binding => (binding.SourceAssetId, binding.InstanceEntityId));
        var entityImportOrigins = new Dictionary<EntityId, ContentImportBatch>();
        var groupImportOrigins = new Dictionary<GroupId, ContentImportBatch>();
        foreach (var batch in workspace.Content.ImportBatches.OrderBy(value => value.ImportedUnixMilliseconds))
        {
            foreach (var stableId in batch.Added)
            {
                if (stableId.StartsWith("entity:", StringComparison.Ordinal))
                {
                    var value = stableId["entity:".Length..];
                    if (Guid.TryParseExact(value, "D", out var parsed) && parsed.ToString("D") == value)
                        entityImportOrigins[new(parsed)] = batch;
                }
                else if (stableId.StartsWith("group:", StringComparison.Ordinal))
                {
                    var value = stableId["group:".Length..];
                    if (Guid.TryParseExact(value, "D", out var parsed) && parsed.ToString("D") == value)
                        groupImportOrigins[new(parsed)] = batch;
                }
            }
        }
        var mobyPVarIndex = _mobyPVarResolver is null ? null : CreateMobyPVarResolutionIndex(workspace.Content);
        var entities = workspace.Content.Entities.Select(entity =>
        {
            var state = entity.State ?? new();
            return new EditorEntitySnapshot(
                entity.EntityId,
                entity.Name,
                entity.Layer,
                entity.Transform,
                entity.Asset,
                entity.Provenance,
                entity.Source?.ClassId,
                Visualization(entity),
                TransformCapabilities(entity),
                new(EntityIsDirty(entity, bindings),
                    state.Hidden, state.Disabled, state.Locked,
                    IsReadOnlySource(entity),
                    HasInvalidGeometryLinks(entity),
                    entity.Asset is not null && _missingAssets.Contains(entity.Asset.Id)),
                entity.SkyShell,
                entity.Collision,
                entity.Tfrag,
                entity.Asset is { } sharedAsset && sharedAsset.IsInstancedCollisionSource()
                    && bindings.TryGetValue((sharedAsset.Id, null), out var sharedBinding)
                        ? new(sharedBinding.ProxyAssetId, sharedBinding.Recipe, sharedBinding.FaceTypeOverrides)
                        : null,
                entity.Asset is { } individualAsset && individualAsset.IsInstancedCollisionSource()
                    && bindings.TryGetValue((individualAsset.Id, entity.EntityId), out var individualBinding)
                        ? new(individualBinding.ProxyAssetId, individualBinding.Recipe,
                            individualBinding.FaceTypeOverrides)
                        : null,
                entity.Asset.IsInstancedCollisionSource()
                    ? entity.InstancedCollisionEnabled
                    : null,
                _mobyPropertyResolver?.Invoke(entity),
                _mobyPVarResolver?.Invoke(
                    workspace, entity, mobyPVarIndex!, selection.Contains(entity.EntityId)),
                _sourceClassNameResolver?.Invoke(workspace, entity),
                entityImportOrigins.TryGetValue(entity.EntityId, out var import)
                    ? new(EditorContentOriginKind.Imported, import.BatchId, import.SourceLevel)
                    : new(entity.Provenance?.Level == workspace.Manifest.BaseLevel.Level
                        ? EditorContentOriginKind.Base
                        : EditorContentOriginKind.Project));
        }).ToArray();
        var entitiesById = entities.ToDictionary(entity => entity.EntityId);
        var references = ReferenceSnapshots(workspace, entitiesById);
        var groups = workspace.Content.Groups.Select(group => new EditorGroupSnapshot(
            group.GroupId,
            group.Name,
            group.Members,
            group.Members.Where(member => !entitiesById.ContainsKey(member)).ToArray(),
            group.Template,
            groupImportOrigins.TryGetValue(group.GroupId, out var import)
                ? new(EditorContentOriginKind.Imported, import.BatchId, import.SourceLevel)
                : new(EditorContentOriginKind.Project))).ToArray();
        return new(
            workspace.RootPath,
            workspace.Manifest.ProjectId,
            workspace.Manifest.Name,
            workspace.Manifest.Target,
            workspace.Manifest.BaseLevel,
            workspace.Content.LevelSettings,
            HudSnapshot(workspace),
            FxSnapshot(workspace),
            workspace.Content.CustomModels.Models.Select(model => new EditorCustomModelSnapshot(
                model.Canonical.Id,
                model.Native.Id,
                model.SourceName,
                workspace.Content.CustomClasses.Allocations.Single(value => value.Asset == model.Canonical).ClassId,
                model.Metadata)).ToArray(),
            entities,
            references,
            groups,
            MapGroupSnapshots(workspace),
            _groupTemplates,
            workspace.Content.ImportBatches.Select(batch => new EditorImportBatchSnapshot(
                batch.BatchId,
                batch.ImportedUnixMilliseconds,
                batch.SourceGame,
                batch.SourceRegion,
                batch.SourceRevision,
                batch.SourceFingerprint,
                batch.SourceLevel,
                batch.Families,
                batch.Added.Count,
                batch.Reused.Count,
                batch.Replaced.Count,
                batch.Protected.Count)).ToArray(),
            _selection.ToArray(),
            workspace.IsDirty,
            _history.CanUndo,
            _history.CanRedo,
            _clipboard.Length > 0,
            _eventSequence,
            RuntimeCapabilities.Where(HasCapability).ToArray(),
            RuntimeTools.ToArray(),
            _diagnostics.Concat(PlacementDiagnostics(workspace)).Concat(GroupDiagnostics(groups))
                .TakeLast(MaxDiagnostics).ToArray());
    }

    private bool EntityIsDirty(
        ProjectEntity entity,
        IReadOnlyDictionary<(AssetId SourceAssetId, EntityId? InstanceEntityId), ProjectInstancedCollisionBinding>
            ? bindings)
    {
        if (!_savedEntities.TryGetValue(entity.EntityId, out var saved) || saved != entity) return true;
        if (!entity.Asset.IsInstancedCollisionSource()) return false;
        if (bindings is null) throw new InvalidOperationException("Instanced collision bindings are unavailable.");
        var sharedBindingKey = (entity.Asset.Id, (EntityId?)null);
        var individualBindingKey = (entity.Asset.Id, (EntityId?)entity.EntityId);
        return !Equals(
                _savedInstancedCollisionBindings.GetValueOrDefault(sharedBindingKey),
                bindings.GetValueOrDefault(sharedBindingKey))
            || !Equals(
                _savedInstancedCollisionBindings.GetValueOrDefault(individualBindingKey),
                bindings.GetValueOrDefault(individualBindingKey));
    }

    private static IEnumerable<EditorDiagnostic> PlacementDiagnostics(ForgeProjectWorkspace workspace) =>
        workspace.Content.Entities
            .Where(entity => entity.Asset?.Kind == AssetKind.Moby && entity.Provenance is null
                && entity.Source?.RawRecord.Length >= 0x6c
                && System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(
                    entity.Source.RawRecord.AsSpan(0x68)) == -1)
            .Select(entity => new EditorDiagnostic(
                "moby.pvar-not-generated",
                EditorDiagnosticSeverity.Warning,
                $"{entity.Name}: No generated Pvar data; class-specific behavior may be unavailable."));

    private static EditorEntityGeometry? Visualization(ProjectEntity entity)
    {
        var geometry = entity.Geometry;
        if (geometry?.Cuboid is not null) return new(EditorGeometryKind.Cuboid, []);
        if (geometry?.Spline is not null) return new(EditorGeometryKind.Spline, geometry.Spline.Points);
        if (geometry?.Area is not null) return new(EditorGeometryKind.Area, []);
        if (geometry?.Sphere is not null) return new(EditorGeometryKind.Sphere, []);
        if (geometry?.Cylinder is not null) return new(EditorGeometryKind.Cylinder, []);
        if (geometry?.Pill is not null) return new(EditorGeometryKind.Pill, []);
        if (geometry?.GrindPath is not null) return new(EditorGeometryKind.GrindPath, geometry.GrindPath.Points);
        var lighting = entity.Lighting;
        if (lighting?.DirectionalLight is { } directional)
            return new(EditorGeometryKind.DirectionalLight,
            [
                new(0, 0, 0, 1),
                new(directional.TopDirection.X * 12, directional.TopDirection.Y * 12,
                    directional.TopDirection.Z * 12, directional.TopDirection.W),
            ]);
        if (lighting?.PointLight is not null) return new(EditorGeometryKind.PointLight, []);
        if (lighting?.EnvironmentSamplePoint is not null) return new(EditorGeometryKind.EnvironmentSample, []);
        if (lighting?.EnvironmentTransition is not null) return new(EditorGeometryKind.EnvironmentTransition, []);
        if (entity.Camera is not null) return new(EditorGeometryKind.Camera, []);
        if (entity.AmbientSound is not null) return new(EditorGeometryKind.AmbientSound, []);
        return null;
    }

    private static bool HasInvalidGeometryLinks(ProjectEntity entity)
    {
        var area = entity.Geometry?.Area;
        return area is not null && area.Splines.Concat(area.Cuboids).Concat(area.Spheres)
            .Concat(area.Cylinders).Concat(area.NegativeCuboids)
            .Any(link => link.Reference?.EntityId is null);
    }
}
