using Forge.Host.Domain;

namespace Forge.Host.Games.UYA;

internal static partial class UyaContentImportPlanningService
{
    private static IReadOnlyDictionary<string, IReadOnlyList<TargetItem>> TargetItemsByFamily(
        ForgeProjectContent target)
    {
        var result = FamilyOrder.ToDictionary(
            family => family,
            _ => new List<TargetItem>(),
            StringComparer.Ordinal);
        foreach (var entity in target.Entities)
        {
            if (Family(entity) is not { } family) continue;
            result[family].Add(new(
                $"entity:{entity.EntityId}", entity.Asset?.Id.ToString(), entity.Source?.ClassId, entity));
        }
        result["mapGroups"].Add(new("mapGroups:project", null, null, null));
        if (target.Hud is not null) result["hud"].Add(new("hud:project", null, null, null));
        if (target.Fx is not null) result["fx"].Add(new("fx:project", null, null, null));
        if (target.LevelSettings is not null)
            result["levelSettings"].Add(new("settings:project", null, null, null));
        result["occlusion"].Add(new("occlusion:project", null, null, null));
        return result.ToDictionary(
            value => value.Key,
            value => (IReadOnlyList<TargetItem>)value.Value,
            StringComparer.Ordinal);
    }

    private static string[] ProtectionReasons(
        ProjectEntity entity,
        IReadOnlySet<string> replacement,
        ProjectReferenceGraph graph,
        IReadOnlyDictionary<EntityId, ProjectGroup[]> groupsByMember,
        IReadOnlySet<EntityId> mapGroupMembers,
        IReadOnlyDictionary<EntityId, string[]> levelSettingsByEntity,
        IReadOnlySet<string> replaceableGroups)
    {
        var result = new List<string>();
        if (entity.State?.Locked == true) result.Add("entity.state.locked");
        result.AddRange(graph.Incoming(entity.EntityId)
            .Where(edge => !replacement.Contains($"entity:{edge.OwnerEntityId}"))
            .Select(edge => $"entity:{edge.OwnerEntityId} field {edge.Reference.FieldKey}"));
        if (groupsByMember.TryGetValue(entity.EntityId, out var groups))
            result.AddRange(groups.Where(value => !replaceableGroups.Contains($"group:{value.GroupId}"))
                .Select(value => $"group:{value.GroupId} field group.members"));
        if (mapGroupMembers.Contains(entity.EntityId))
            result.Add("mapGroups:project field group.members");
        if (levelSettingsByEntity.TryGetValue(entity.EntityId, out var settings)) result.AddRange(settings);
        return result.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static Dictionary<EntityId, string[]> LevelSettingsProtection(ProjectLevelSettings? settings)
    {
        if (settings?.Scene is not { } scene) return [];
        return new[]
            {
                (ProjectReferences.LevelShipPath, scene.ShipPath.Reference.EntityId),
                (ProjectReferences.LevelShipCameraCuboidStart, scene.ShipCameraCuboidStart.Reference.EntityId),
                (ProjectReferences.LevelShipCameraCuboidEnd, scene.ShipCameraCuboidEnd.Reference.EntityId),
            }
            .Where(value => value.EntityId is not null)
            .GroupBy(value => value.EntityId!.Value)
            .ToDictionary(
                group => group.Key,
                group => group.Select(value => $"settings:project field {value.Item1}").ToArray());
    }

    private static HashSet<string> ImportedGroupStableIds(ForgeProjectContent content) =>
        content.ImportBatches.SelectMany(batch => batch.Added)
            .Where(value => value.StartsWith("group:", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

    private static void AddMapGroupDependencyBlockers(IList<ContentImportFamilyPlan> plans)
    {
        var mapGroupsIndex = plans.Select((value, index) => (value, index))
            .Single(value => value.value.Family == "mapGroups").index;
        var mapGroups = plans[mapGroupsIndex];
        if (!mapGroups.Selected) return;
        var blockers = mapGroups.Blockers.ToList();
        foreach (var (marker, family, label) in new[]
        {
            ("source:mapGroups:moby:table", "mobys", "Mobys"),
            ("source:mapGroups:tie:table", "ties", "TIEs"),
            ("source:mapGroups:shrub:table", "shrubs", "shrubs"),
        })
        {
            if (!mapGroups.Add.Contains(marker)) continue;
            var members = plans.Single(value => value.Family == family);
            if (!members.Selected || members.Mode != ContentImportMode.Replace)
                blockers.Add($"Map-group replacement requires {label} to be selected in replace mode.");
            else if (members.Protect.Count > 0)
                blockers.Add($"Map-group replacement requires every target {label} instance to be replaceable.");
        }
        plans[mapGroupsIndex] = mapGroups with { Blockers = Limit(blockers) };
    }

    private static void AddOcclusionDependencyBlockers(IList<ContentImportFamilyPlan> plans)
    {
        var occlusionIndex = plans.Select((value, index) => (value, index))
            .Single(value => value.value.Family == "occlusion").index;
        var occlusion = plans[occlusionIndex];
        if (!occlusion.Selected) return;
        var blockers = occlusion.Blockers.ToList();
        foreach (var (family, label) in new[]
        {
            ("ties", "TIEs"),
            ("mobys", "Mobys"),
            ("tfrags", "tfrags"),
        })
        {
            var dependency = plans.Single(value => value.Family == family);
            if (!dependency.Selected || dependency.Mode != ContentImportMode.Replace)
                blockers.Add($"Occlusion replacement requires {label} to be selected in replace mode.");
            else if (dependency.Protect.Count > 0)
                blockers.Add($"Occlusion replacement requires every target {label} item to be replaceable.");
        }
        plans[occlusionIndex] = occlusion with { Blockers = Limit(blockers) };
    }

    private static void ExpandTfragOwnerProtection(
        IReadOnlyList<TargetItem> target,
        IDictionary<string, string[]> protection)
    {
        foreach (var owner in target.Where(value => value.Entity?.Tfrag is not null).GroupBy(value =>
                     (value.Entity!.Tfrag!.SourceKind, value.Entity.Tfrag.SourceIndex)))
        {
            var protectedMember = owner.FirstOrDefault(value => protection[value.Key].Length > 0);
            if (protectedMember is null) continue;
            var reason = $"tfrag owner {owner.Key.SourceKind.ToString().ToLowerInvariant()}:{owner.Key.SourceIndex}";
            foreach (var member in owner)
                protection[member.Key] = protection[member.Key].Append(reason)
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        }
    }

    private static string[] ClassConflicts(
        IReadOnlyList<UyaContentImportSourceItem> source,
        IReadOnlyList<TargetItem> target)
    {
        var targetClasses = target.Where(value => value.ClassId is not null && value.AssetId is not null)
            .GroupBy(value => value.ClassId!.Value)
            .ToDictionary(group => group.Key, group => group.Select(value => value.AssetId!).ToHashSet(StringComparer.Ordinal));
        return source.Where(value => value.ClassId is not null && value.AssetId is not null
                && targetClasses.TryGetValue(value.ClassId.Value, out var assets) && !assets.Contains(value.AssetId))
            .Select(value => $"class:0x{value.ClassId!.Value:X4}")
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static UyaContentImportSourceItem SourceItem(ProjectEntity entity, string family)
    {
        var provenance = entity.Provenance ?? throw new InvalidDataException("Donor entity has no source provenance.");
        return new(
            $"source:{family}:{provenance.Section}:{provenance.SourceIndex}",
            entity.Asset?.Id.ToString(),
            entity.Source?.ClassId);
    }

    private static string? Family(ProjectEntity entity) => entity switch
    {
        { Layer: "ties" } => "ties",
        { Layer: "shrubs" } => "shrubs",
        { Layer: "tfrags" } => "tfrags",
        { Layer: "mobys" } => "mobys",
        { Collision: not null } => "collision",
        { Geometry.Cuboid: not null } => "cuboids",
        { Geometry.Sphere: not null } => "spheres",
        { Geometry.Spline: not null } => "splines",
        { Geometry.GrindPath: not null } => "paths",
        { Geometry.Area: not null } => "areas",
        { SkyShell: not null } => "skybox",
        { Lighting.DirectionalLight: not null } => "directionalLights",
        _ => null,
    };

    private static long EstimateBytes(UyaBaseLevelData source, string family) => family switch
    {
        "ties" => source.Entities.Where(value => value.Layer == "ties").Sum(value => (long)(value.Source?.RawRecord.Length ?? 0)),
        "shrubs" => source.Entities.Where(value => value.Layer == "shrubs").Sum(value => (long)(value.Source?.RawRecord.Length ?? 0)),
        "mobys" => source.Entities.Where(value => value.Layer == "mobys").Sum(value => (long)(value.Source?.RawRecord.Length ?? 0)),
        "tfrags" => LayerBytes(source, BakeLayerId.Tfrags),
        "collision" => LayerBytes(source, BakeLayerId.Collision),
        "skybox" => LayerBytes(source, BakeLayerId.Sky),
        "hud" => source.OpaqueSections.Where(value => value.Name == "level-data/hud-header"
                || value.Name.StartsWith("level-data/hud-bank-", StringComparison.Ordinal))
            .Sum(value => (long)value.Bytes.Length),
        "fx" => source.Fx?.SourceTextures.Sum(value => 0x400L + Math.Max(0L, (long)value.Width * value.Height)) ?? 0,
        "occlusion" => source.Occlusion is null ? 0 : UyaOcclusionImportPayload.Encode(source.Occlusion).LongLength,
        "mapGroups" => source.OpaqueSections.Where(value => value.Name.EndsWith("_groups", StringComparison.Ordinal)).Sum(value => (long)value.Bytes.Length),
        "opaqueContent" => source.OpaqueSections.Where(value => !value.Name.EndsWith("_groups", StringComparison.Ordinal)).Sum(value => (long)value.Bytes.Length),
        _ => source.Gameplay.Blocks.Where(value => GameplayFamily(value.SemanticName) == family).Sum(value => (long)value.PayloadBytes.Length),
    };

    private static long LayerBytes(UyaBaseLevelData source, BakeLayerId layer) =>
        source.BaseLayers.Where(value => value.Layer == layer).Sum(value => (long)value.Bytes.Length);

    private static bool SourceSectionPresent(UyaBaseLevelData source, string family) => family switch
    {
        "ties" or "shrubs" or "mobys" => true,
        "tfrags" => source.BaseLayers.Any(value => value.Layer == BakeLayerId.Tfrags),
        "collision" => source.BaseLayers.Any(value => value.Layer == BakeLayerId.Collision),
        "skybox" => source.BaseLayers.Any(value => value.Layer == BakeLayerId.Sky),
        "mapGroups" => source.OpaqueSections.Any(value => value.Name.EndsWith("_groups", StringComparison.Ordinal)),
        "levelSettings" => source.Gameplay.Blocks.Any(value => value.SemanticName == "level_settings"),
        "hud" => source.Hud is not null,
        "fx" => source.Fx is not null,
        "occlusion" => source.Occlusion is not null,
        "opaqueContent" => source.OpaqueSections.Count > 0,
        _ => source.Gameplay.Blocks.Any(value => GameplayFamily(value.SemanticName) == family),
    };

    private static string? GameplayFamily(string section) => section switch
    {
        "cuboids" => "cuboids",
        "spheres" => "spheres",
        "splines" => "splines",
        "grind_splines" => "paths",
        "areas" => "areas",
        "directional_lights" => "directionalLights",
        "level_settings" => "levelSettings",
        _ => null,
    };

    internal static IReadOnlyList<string> SourceWarnings(
        UyaBaseLevelData source,
        int externalMobyReferenceCount)
    {
        var result = new List<string>();
        if (source.MissingInstanceCount > 0)
            result.Add($"{source.MissingInstanceCount} donor instances have missing catalog assets.");
        if (source.FallbackTransformCount > 0)
            result.Add($"{source.FallbackTransformCount} donor transforms require fallback values.");
        if (externalMobyReferenceCount > 0)
            result.Add($"{externalMobyReferenceCount} donor Moby PVar reference(s) require an explicit map or clear choice before import.");
        return result;
    }
}
