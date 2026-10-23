using Forge.Host.Domain;

namespace Forge.Host.Games.UYA;

internal sealed record UyaContentImportRemovalPreparation(
    ContentImportRemovalPlan Plan,
    ForgeProjectContent Content);

internal static class UyaContentImportRemovalService
{
    public static UyaContentImportRemovalPreparation Prepare(
        ForgeProjectWorkspace workspace,
        string batchId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        cancellationToken.ThrowIfCancellationRequested();
        var batch = workspace.Content.ImportBatches.SingleOrDefault(value => value.BatchId == batchId)
            ?? throw new InvalidOperationException($"Import batch {batchId} was not found.");
        var modes = batch.Families.ToDictionary(value => value.Family, value => value.Mode, StringComparer.Ordinal);
        var added = batch.Added.ToHashSet(StringComparer.Ordinal);
        var survivors = new Dictionary<string, string>(StringComparer.Ordinal);
        var entitiesById = workspace.Content.Entities.ToDictionary(value => value.EntityId);
        var ownedEntityIds = StableEntityIds(added).Where(entitiesById.ContainsKey).ToHashSet();
        var removableEntities = new HashSet<EntityId>();
        foreach (var id in ownedEntityIds)
        {
            var entity = entitiesById[id];
            var family = Family(entity);
            var stableId = EntityStableId(id);
            if (family is null || !modes.TryGetValue(family, out var mode))
                survivors[stableId] = "Its owning import family cannot be identified safely.";
            else if (mode != ContentImportMode.Add)
                survivors[stableId] = "Replace imports cannot restore the displaced target content.";
            else if (entity.State?.Locked == true)
                survivors[stableId] = "The entity is locked.";
            else
                removableEntities.Add(id);
        }

        var ownedGroupIds = StableGroupIds(added).ToHashSet();
        var existingOwnedGroups = workspace.Content.Groups
            .Where(value => ownedGroupIds.Contains(value.GroupId)).ToArray();
        ProtectEntityDependencies(workspace.Content, removableEntities, existingOwnedGroups, survivors);
        cancellationToken.ThrowIfCancellationRequested();

        var removedEntities = removableEntities;
        var skyOrder = workspace.Content.Entities.Where(value => !removedEntities.Contains(value.EntityId)
                && value.SkyShell is not null)
            .OrderBy(value => value.SkyShell!.Order)
            .Select((value, index) => (value.EntityId, index))
            .ToDictionary(value => value.EntityId, value => value.index);
        var retainedEntities = workspace.Content.Entities.Where(value => !removedEntities.Contains(value.EntityId))
            .Select(value => value.SkyShell is null ? value : value with
            {
                SkyShell = value.SkyShell with { Order = skyOrder[value.EntityId] },
            }).ToArray();
        var retainedEntityIds = retainedEntities.Select(value => value.EntityId).ToHashSet();
        var removedGroups = existingOwnedGroups
            .Where(value => value.Members.All(removedEntities.Contains)).Select(value => value.GroupId).ToHashSet();
        foreach (var group in existingOwnedGroups.Where(value => !removedGroups.Contains(value.GroupId)))
            survivors[GroupStableId(group.GroupId)] = "The group contains retained members.";

        var content = workspace.Content with
        {
            Entities = retainedEntities,
            Groups = workspace.Content.Groups.Where(value => !removedGroups.Contains(value.GroupId)).ToArray(),
            InstancedCollisionBindings = workspace.Content.InstancedCollisionBindings.Where(binding =>
                binding.InstanceEntityId is { } instanceId
                    ? retainedEntityIds.Contains(instanceId)
                    : retainedEntities.Any(entity => entity.Asset?.Id == binding.SourceAssetId)).ToArray(),
            MobyPVars = ProjectMobyPVars.RemoveAndClear(workspace.Content.MobyPVars, removedEntities),
        };

        content = RemoveTfragOwners(content, added, survivors);
        content = RemoveMobyMetadata(content, added, survivors);
        content = RemoveSoundContent(content, added, survivors);
        content = RemoveCustomClasses(content, added, survivors);
        content = RemoveSettings(content, batch, added, survivors);
        content = RemoveAssets(content, added, survivors);
        ForgeProjectValidation.Validate(workspace.RootPath, workspace.Manifest, content);

        var originalIds = LiveStableIds(workspace.Content);
        var retainedIds = LiveStableIds(content);
        var deleted = batch.Added.Where(value => originalIds.Contains(value) && !retainedIds.Contains(value))
            .Order(StringComparer.Ordinal).ToArray();
        foreach (var stableId in batch.Added.Where(retainedIds.Contains))
            survivors.TryAdd(stableId, "The content is still referenced or is not safely reversible.");
        var warnings = batch.Families.Where(value => value.Mode == ContentImportMode.Replace)
            .Select(value => $"{value.Family} used Replace; Forge cannot restore the displaced target content because it is not stored in the batch ledger.")
            .ToArray();
        var plan = new ContentImportRemovalPlan(
            ContentImportRemovalSchema.CurrentVersion,
            batch.BatchId,
            workspace.CurrentFingerprint,
            deleted,
            survivors.Where(value => retainedIds.Contains(value.Key))
                .Select(value => new ContentImportRemovalSurvivor(value.Key, value.Value))
                .OrderBy(value => value.StableId, StringComparer.Ordinal).ToArray(),
            batch.Reused,
            batch.Protected,
            warnings);
        return new(plan, content);
    }

    public static async Task<EditorSnapshot> ApplyAsync(
        EditorRuntime runtime,
        string projectPath,
        string batchId,
        string targetFingerprint,
        CancellationToken cancellationToken = default)
    {
        var prepared = await runtime.ReadWorkspaceAsync(projectPath, (workspace, token) =>
        {
            var value = Prepare(workspace.CreateProjection(), batchId, token);
            return Task.FromResult(value);
        }, cancellationToken);
        if (!string.Equals(prepared.Plan.TargetFingerprint, targetFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("The import-removal preview changed; review it again before removing content.");
        if (prepared.Plan.Delete.Count == 0)
            throw new InvalidOperationException("This import has no dependency-safe content to remove.");
        return await runtime.CommitContentImportRemovalAsync(
            new(targetFingerprint, batchId, prepared.Content), cancellationToken);
    }

    private static void ProtectEntityDependencies(
        ForgeProjectContent content,
        HashSet<EntityId> removable,
        IReadOnlyList<ProjectGroup> ownedGroups,
        IDictionary<string, string> survivors)
    {
        var graph = ProjectReferenceGraph.Create(content);
        var ownedGroupIds = ownedGroups.Select(value => value.GroupId).ToHashSet();
        var entitiesById = content.Entities.ToDictionary(value => value.EntityId);
        var groupsByMember = content.Groups.SelectMany(group => group.Members.Select(member => (member, group)))
            .GroupBy(value => value.member)
            .ToDictionary(group => group.Key, group => group.Select(value => value.group).ToArray());
        var mapGroupMembers = content.MapGroups.SelectMany(table => table.Groups)
            .SelectMany(group => group.Members).ToHashSet();
        var tfragGroupsByMember = content.Entities.Where(value => value.Tfrag is not null)
            .GroupBy(value => (value.Tfrag!.SourceKind, value.Tfrag.SourceIndex))
            .Select(group => group.Select(value => value.EntityId).ToArray())
            .SelectMany(members => members.Select(entityId => (EntityId: entityId, Members: members)))
            .ToDictionary(value => value.EntityId, value => value.Members);
        var collisionOwnersByLinkedEntity = content.Entities.Where(entity => entity.Collision is not null)
            .SelectMany(entity => (entity.Collision!.LinkedInstanceEntityIds ?? [])
                .Select(linked => (linked, entity)))
            .GroupBy(value => value.linked)
            .ToDictionary(group => group.Key, group => group.Select(value => value.entity).ToArray());
        var pending = new Queue<EntityId>();
        foreach (var id in removable.ToArray())
        {
            if (graph.Incoming(id).FirstOrDefault(edge => !removable.Contains(edge.OwnerEntityId)) is { } edge)
                Protect(id, $"Referenced by entity {edge.OwnerEntityId} field {edge.Reference.FieldKey}.");
            else if (groupsByMember.GetValueOrDefault(id)?.FirstOrDefault(group =>
                !ownedGroupIds.Contains(group.GroupId) || group.Members.Any(member => !removable.Contains(member))) is { } group)
                Protect(id, $"Referenced by group {group.GroupId}.");
            else if (mapGroupMembers.Contains(id))
                Protect(id, "Referenced by the authoritative map-group table.");
            else if (LevelSettingReference(content.LevelSettings, id) is { } settingReason)
                Protect(id, settingReason);
            else if (collisionOwnersByLinkedEntity.GetValueOrDefault(id)?.FirstOrDefault(entity =>
                !removable.Contains(entity.EntityId)) is { } collision)
                Protect(id, $"Referenced by collision entity {collision.EntityId}.");
            else if (tfragGroupsByMember.GetValueOrDefault(id)?.Any(member => !removable.Contains(member)) == true)
                Protect(id, "Its tfrag owner must be retained as a complete unit.");
        }

        while (pending.TryDequeue(out var ownerId))
        {
            foreach (var edge in graph.Outgoing(ownerId).Where(value => value.Reference.EntityId is not null))
                Protect(edge.Reference.EntityId!.Value,
                    $"Referenced by entity {ownerId} field {edge.Reference.FieldKey}.");
            if (entitiesById.GetValueOrDefault(ownerId)?.Collision?.LinkedInstanceEntityIds is { } links)
                foreach (var id in links) Protect(id, $"Referenced by collision entity {ownerId}.");
            foreach (var group in groupsByMember.GetValueOrDefault(ownerId) ?? [])
                foreach (var id in group.Members) Protect(id, $"Referenced by group {group.GroupId}.");
            foreach (var id in tfragGroupsByMember.GetValueOrDefault(ownerId) ?? [])
                Protect(id, "Its tfrag owner must be retained as a complete unit.");
        }

        void Protect(EntityId id, string reason)
        {
            if (!removable.Remove(id)) return;
            survivors[EntityStableId(id)] = reason;
            pending.Enqueue(id);
        }
    }

    private static string? LevelSettingReference(ProjectLevelSettings? settings, EntityId id)
    {
        if (settings?.Scene is not { } scene) return null;
        if (scene.ShipPath.Reference.EntityId == id) return $"Referenced by level settings field {ProjectReferences.LevelShipPath}.";
        if (scene.ShipCameraCuboidStart.Reference.EntityId == id)
            return $"Referenced by level settings field {ProjectReferences.LevelShipCameraCuboidStart}.";
        if (scene.ShipCameraCuboidEnd.Reference.EntityId == id)
            return $"Referenced by level settings field {ProjectReferences.LevelShipCameraCuboidEnd}.";
        return null;
    }

    private static ForgeProjectContent RemoveTfragOwners(
        ForgeProjectContent content,
        IReadOnlySet<string> added,
        IDictionary<string, string> survivors)
    {
        if (content.Tfrags is null) return content;
        var retainedEntityIds = content.Entities.Select(value => value.EntityId).ToHashSet();
        var owners = content.Tfrags.ImportedOwners.Where(owner =>
        {
            var stableId = $"owner:{owner.OwnerId}";
            if (!added.Contains(stableId)) return true;
            if (!owner.Pieces.Any(retainedEntityIds.Contains)) return false;
            survivors[stableId] = "The tfrag owner still has retained pieces.";
            return true;
        }).ToArray();
        var sources = content.Tfrags.Sources.Select(source => source with
        {
            GroupCount = content.Entities.Count(entity => entity.Tfrag is { } piece
                && piece.SourceKind == source.SourceKind && piece.SourceIndex == source.SourceIndex),
        }).ToArray();
        return content with { Tfrags = content.Tfrags with { ImportedOwners = owners, Sources = sources } };
    }

    private static ForgeProjectContent RemoveMobyMetadata(
        ForgeProjectContent content,
        IReadOnlySet<string> added,
        IDictionary<string, string> survivors)
    {
        if (content.MobyDex is null) return content;
        var entries = content.MobyDex.Entries.Where(entry =>
        {
            var stableId = $"mobydex:{entry.Game}:{entry.OClass}";
            if (!added.Contains(stableId)) return true;
            if (!content.Entities.Any(entity => entity.Layer == "mobys" && entity.Source?.ClassId == entry.OClass))
                return false;
            survivors[stableId] = "A retained Moby still uses this MobyDex entry.";
            return true;
        }).ToArray();
        return content with { MobyDex = entries.Length == 0 ? null : content.MobyDex with { Entries = entries } };
    }

    private static ForgeProjectContent RemoveSoundContent(
        ForgeProjectContent content,
        IReadOnlySet<string> added,
        IDictionary<string, string> survivors)
    {
        var mobyClasses = content.Entities.Where(value => value.Layer == "mobys" && value.Source is not null)
            .Select(value => value.Source!.ClassId).ToHashSet();
        var maps = content.Sounds.MobyRemaps.Where(value => mobyClasses.Contains(value.ClassId)).ToArray();
        var candidatePrograms = content.Sounds.Programs.Where(value => value.Asset is not null
                && added.Contains($"sound:{value.Asset.Id}"))
            .Select(value => value.Asset!.Id).ToHashSet();
        var roots = maps.SelectMany(value => value.Slots).Concat(content.Sounds.GlobalRemaps)
            .Where(value => value.Sound is not null).Select(value => value.Sound!.Id)
            .Concat(content.Sounds.Programs.Where(value => value.Asset is not null
                    && !candidatePrograms.Contains(value.Asset.Id))
                .Select(value => value.Asset!.Id)).ToHashSet();
        var programs = content.Sounds.Programs.Where(value => value.Asset is not null)
            .ToDictionary(value => value.Asset!.Id);
        var retainedPrograms = new HashSet<AssetId>();
        foreach (var root in roots) Visit(root);
        foreach (var id in candidatePrograms.Where(retainedPrograms.Contains))
            survivors[$"sound:{id}"] = "The sound program remains reachable from a retained remap or program.";
        var keptPrograms = content.Sounds.Programs.Where(value => value.Asset is null
            || !candidatePrograms.Contains(value.Asset.Id) || retainedPrograms.Contains(value.Asset.Id)).ToArray();
        var usedSamples = keptPrograms.SelectMany(value => value.Operations).Where(value => value.Tone is not null)
            .Select(value => value.Tone!.Sample.Id).ToHashSet();
        var candidateAssets = StableAssetIds(added).ToHashSet();
        var samples = content.Sounds.Samples.Where(value => !candidateAssets.Contains(value.Asset.Id)
            || usedSamples.Contains(value.Asset.Id)).ToArray();
        return content with { Sounds = content.Sounds with { Programs = keptPrograms, Samples = samples, MobyRemaps = maps } };

        void Visit(AssetId id)
        {
            if (!retainedPrograms.Add(id) || !programs.TryGetValue(id, out var program)) return;
            foreach (var child in program.Operations.Where(value => value.ReferencedProgram is not null)
                .Select(value => value.ReferencedProgram!.Id)) Visit(child);
        }
    }

    private static ForgeProjectContent RemoveCustomClasses(
        ForgeProjectContent content,
        IReadOnlySet<string> added,
        IDictionary<string, string> survivors)
    {
        var entityAssets = content.Entities.Where(value => value.Asset is not null)
            .Select(value => value.Asset!).ToHashSet();
        var soundMobys = content.Sounds.MobyRemaps.Select(value => value.Moby).ToHashSet();
        var allocations = content.CustomClasses.Allocations.Where(value =>
        {
            var stableId = $"class:{value.Asset.Kind.ToString().ToLowerInvariant()}:{value.ClassId}";
            if (!added.Contains(stableId)) return true;
            if (!entityAssets.Contains(value.Asset) && !soundMobys.Contains(value.Asset)) return false;
            survivors[stableId] = "A retained entity or sound map still uses this class allocation.";
            return true;
        }).ToArray();
        return content with { CustomClasses = content.CustomClasses with { Allocations = allocations } };
    }

    private static ForgeProjectContent RemoveSettings(
        ForgeProjectContent content,
        ContentImportBatch batch,
        IReadOnlySet<string> added,
        IDictionary<string, string> survivors)
    {
        if (!added.Contains("settings:project") || content.LevelSettings is null) return content;
        if (batch.Families.Any(value => value is { Family: "levelSettings", Mode: ContentImportMode.Add }))
            return content with { LevelSettings = null };
        survivors["settings:project"] = "Replace imports cannot restore the displaced level settings.";
        return content;
    }

    private static ForgeProjectContent RemoveAssets(
        ForgeProjectContent content,
        IReadOnlySet<string> added,
        IDictionary<string, string> survivors)
    {
        var candidates = StableAssetIds(added).ToHashSet();
        var referenced = ReferencedAssets(content);
        var byId = content.Assets.ToDictionary(value => value.Id);
        var pending = new Queue<AssetId>(referenced);
        while (pending.TryDequeue(out var id))
            if (byId.TryGetValue(id, out var asset) && asset.ParentId is { } parent && referenced.Add(parent))
                pending.Enqueue(parent);
        foreach (var id in candidates.Where(referenced.Contains))
            survivors[$"asset:{id}"] = "The attached asset is still referenced by retained content.";
        return content with { Assets = content.Assets.Where(value => !candidates.Contains(value.Id)
            || referenced.Contains(value.Id)).ToArray() };
    }

    private static HashSet<AssetId> ReferencedAssets(ForgeProjectContent content) => content.Entities
        .Where(value => value.Asset is not null).Select(value => value.Asset!.Id)
        .Concat(content.InstancedCollisionBindings.SelectMany(value => new[] { value.SourceAssetId, value.ProxyAssetId }))
        .Concat(content.AssetOverrides.Select(value => value.Replacement.Id))
        .Concat(content.CustomClasses.Allocations.Select(value => value.Asset.Id))
        .Concat(content.CustomModels.Models.SelectMany(ModelAssets))
        .Concat((content.Tfrags?.CustomImports ?? []).SelectMany(CustomTfragAssets))
        .Concat((content.Tfrags?.ImportedOwners ?? []).SelectMany(ImportedTfragAssets))
        .Concat(ProjectSounds.AssetIds(content.Sounds))
        .Concat(content.Hud?.SourcePayload is { } hudSource ? [hudSource.Id] : [])
        .Concat((content.Hud?.SourceIcons ?? []).SelectMany(value => value.Frames)
            .Where(value => value.Texture is not null).Select(value => value.Texture!.Id))
        .Concat((content.Hud?.Additions ?? []).Select(value => value.Texture.Id))
        .Concat((content.Fx?.SourceTextures ?? []).Where(value => value.Texture is not null)
            .Select(value => value.Texture!.Id))
        .Concat((content.Fx?.Additions ?? []).Select(value => value.Texture.Id))
        .Concat(content.Occlusion is null ? [] : [content.Occlusion.SourcePayload.Id])
        .ToHashSet();

    private static IEnumerable<AssetId> ModelAssets(ProjectCustomModel model) => model.Dependencies
        .Select(value => value.Asset.Id).Prepend(model.Source.Id).Append(model.Canonical.Id).Append(model.Native.Id)
        .Concat(model.Textures.Select(value => value.Id));

    private static IEnumerable<AssetId> CustomTfragAssets(ProjectCustomTfragImport value) => value.Dependencies
        .Select(dependency => dependency.Asset.Id).Prepend(value.Source.Id).Concat(value.Textures.Select(texture => texture.Id));

    private static IEnumerable<AssetId> ImportedTfragAssets(ProjectImportedTfragOwner value) =>
        value.Textures.Select(texture => texture.Asset.Id).Prepend(value.NativePayload.Id);

    private static string? Family(ProjectEntity entity) => entity switch
    {
        { Layer: "ties" } => "ties",
        { Layer: "shrubs" } => "shrubs",
        { Layer: "tfrags" } => "tfrags",
        { Layer: "mobys" } => "mobys",
        { Geometry.Cuboid: not null } => "cuboids",
        { Geometry.Sphere: not null } => "spheres",
        { Geometry.Spline: not null } => "splines",
        { Geometry.GrindPath: not null } => "paths",
        { Geometry.Area: not null } => "areas",
        { SkyShell: not null } => "skybox",
        { Lighting.DirectionalLight: not null } => "directionalLights",
        { Collision: not null } => "collision",
        _ => null,
    };

    private static HashSet<string> LiveStableIds(ForgeProjectContent content) => content.Entities
        .Select(value => EntityStableId(value.EntityId))
        .Concat(content.Groups.Select(value => GroupStableId(value.GroupId)))
        .Concat(content.Assets.Select(value => $"asset:{value.Id}"))
        .Concat(content.Sounds.Programs.Where(value => value.Asset is not null)
            .Select(value => $"sound:{value.Asset!.Id}"))
        .Concat((content.Tfrags?.ImportedOwners ?? []).Select(value => $"owner:{value.OwnerId}"))
        .Concat(content.CustomClasses.Allocations.Select(value =>
            $"class:{value.Asset.Kind.ToString().ToLowerInvariant()}:{value.ClassId}"))
        .Concat((content.MobyDex?.Entries ?? []).Select(value => $"mobydex:{value.Game}:{value.OClass}"))
        .Concat(content.LevelSettings is null ? [] : new[] { "settings:project" })
        .Concat(content.Occlusion is null ? [] : new[] { "occlusion:project" })
        .ToHashSet(StringComparer.Ordinal);

    private static IEnumerable<EntityId> StableEntityIds(IEnumerable<string> values) => values
        .Where(value => TryEntityId(value, out _)).Select(value => EntityId.Parse(value["entity:".Length..]));

    private static IEnumerable<GroupId> StableGroupIds(IEnumerable<string> values) => values
        .Where(value => TryGroupId(value, out _)).Select(value => GroupId.Parse(value["group:".Length..]));

    private static IEnumerable<AssetId> StableAssetIds(IEnumerable<string> values) => values
        .Where(value => TryAssetId(value, "asset:", out _)).Select(value => AssetId.Parse(value["asset:".Length..]));

    private static bool TryEntityId(string value, out EntityId id)
    {
        id = default;
        if (!value.StartsWith("entity:", StringComparison.Ordinal)) return false;
        try { id = EntityId.Parse(value["entity:".Length..]); return true; }
        catch (FormatException) { return false; }
    }

    private static bool TryGroupId(string value, out GroupId id)
    {
        id = default;
        if (!value.StartsWith("group:", StringComparison.Ordinal)) return false;
        try { id = GroupId.Parse(value["group:".Length..]); return true; }
        catch (FormatException) { return false; }
    }

    private static bool TryAssetId(string value, string prefix, out AssetId id)
    {
        id = default;
        if (!value.StartsWith(prefix, StringComparison.Ordinal)) return false;
        try { id = AssetId.Parse(value[prefix.Length..]); return true; }
        catch (FormatException) { return false; }
    }

    private static string EntityStableId(EntityId id) => $"entity:{id}";
    private static string GroupStableId(GroupId id) => $"group:{id}";
}
