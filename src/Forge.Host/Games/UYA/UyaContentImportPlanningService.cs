using System.Security.Cryptography;
using System.Text;
using Forge.Host.Domain;
using RatchetPs2.Games.UYA.Gameplay;

namespace Forge.Host.Games.UYA;

public sealed record UyaContentImportPlanRequest(
    string ProjectPath,
    string CatalogRootPath,
    string SourceIsoPath,
    string SourceFingerprint,
    int SourceLevel,
    IReadOnlyList<ContentImportSelection> Selections);

internal sealed record UyaContentImportSourceItem(
    string Key,
    string? AssetId = null,
    int? ClassId = null,
    int ItemCount = 1);

internal sealed record UyaContentImportSourceFamily(
    string Family,
    ContentImportSourceState State,
    IReadOnlyList<UyaContentImportSourceItem> Items,
    long EstimatedBytes,
    string? Diagnostic = null);

internal sealed record UyaContentImportPlanningInput(
    string SourceFingerprint,
    string SourceLevelHash,
    int SourceLevel,
    string TargetProjectId,
    string TargetFingerprint,
    int TargetLevel,
    IReadOnlyList<UyaContentImportSourceFamily> SourceFamilies,
    ForgeProjectContent Target,
    IReadOnlyList<ContentImportSelection> Selections,
    IReadOnlyList<string>? Warnings = null);

internal sealed record UyaContentImportPlanningContext(
    ForgeProjectWorkspace Workspace,
    AssetCatalogStore Catalog,
    UyaBaseLevelData Source,
    ContentImportPlan Plan);

internal static partial class UyaContentImportPlanningService
{
    private static readonly string[] FamilyOrder =
    [
        "ties", "shrubs", "tfrags", "mobys", "collision", "cuboids", "spheres", "splines",
        "paths", "areas", "mapGroups", "skybox", "levelSettings", "directionalLights", "hud", "fx", "occlusion",
        "opaqueContent",
    ];

    public static async Task<ContentImportPlan> PlanAsync(
        UyaContentImportPlanRequest request,
        Func<IsoProgress, ValueTask>? progress = null,
        CancellationToken cancellationToken = default) =>
        (await PrepareAsync(request, progress, cancellationToken)).Plan;

    internal static async Task<UyaContentImportPlanningContext> PrepareAsync(
        UyaContentImportPlanRequest request,
        Func<IsoProgress, ValueTask>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var workspace = await ForgeProjectWorkspace.OpenAsync(request.ProjectPath, cancellationToken);
        return await PrepareAsync(request, workspace, progress, cancellationToken);
    }

    internal static async Task<UyaContentImportPlanningContext> PrepareAsync(
        UyaContentImportPlanRequest request,
        ForgeProjectWorkspace workspace,
        Func<IsoProgress, ValueTask>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Validate(request);
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.Manifest.Target is not { Game: "UYA", Region: "NTSC-U", Revision: "1.00" })
            throw new InvalidDataException("Content import requires a UYA NTSC-U 1.00 target project.");
        var snapshot = await UyaContentImportSourceCache.ReadAsync(
            request, workspace.Manifest.BaseLevel.SourceFingerprint, progress, cancellationToken);
        var identity = snapshot.Identity;
        var catalog = snapshot.Catalog;
        var source = snapshot.Source;

        var result = CreatePlan(new(
            identity.Fingerprint.ToLowerInvariant(),
            source.SourceSha256,
            request.SourceLevel,
            workspace.Manifest.ProjectId.ToString(),
            workspace.CurrentFingerprint,
            workspace.Manifest.BaseLevel.Level,
            snapshot.Families,
            workspace.Content,
            request.Selections,
            snapshot.Warnings));
        result = AddExternalReferenceRequirements(
            result, snapshot.ExternalReferences, workspace.Content);
        result = AddLevelSettingsReferenceRequirements(
            result, source.LevelSettings, source.Entities, workspace.Content);
        result = AddAreaReferenceRequirements(result, source.Entities, workspace.Content);
        result = AddDirectionalLightBlockers(result, source.Entities, workspace.Content);
        if (progress is not null) await progress(new(10_000, 10_000));
        return new(workspace, catalog, source, result);
    }

    internal static ContentImportPlan CreatePlan(
        UyaContentImportPlanningInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.SourceFamilies.Count != FamilyOrder.Length
            || !input.SourceFamilies.Select(value => value.Family).SequenceEqual(FamilyOrder))
            throw new ArgumentException("The donor inventory does not match the UYA family contract.", nameof(input));
        if (input.SourceFamilies.Sum(value => (long)value.Items.Count) > ContentImportPlanSchema.MaximumPlanItems)
            throw new InvalidDataException("The donor inventory exceeds the import-plan item limit.");
        if ((long)input.Target.Entities.Count + input.Target.Groups.Count > ContentImportPlanSchema.MaximumPlanItems)
            throw new InvalidDataException("The target project exceeds the import-plan item limit.");

        var selections = input.Selections.ToDictionary(value => value.Family, StringComparer.Ordinal);
        var supportedFamilies = FamilyOrder.ToHashSet(StringComparer.Ordinal);
        if (selections.Count != input.Selections.Count || selections.Keys.Any(value => !supportedFamilies.Contains(value)))
            throw new ArgumentException("Import selections contain a duplicate or unknown family.", nameof(input));
        if (selections.Values.Any(value => value.Mode is not (ContentImportMode.Add or ContentImportMode.Replace)))
            throw new ArgumentException("Import selections must choose add or replace.", nameof(input));

        var inventory = input.SourceFamilies.Select(value =>
            new ContentImportFamilyInventory(value.Family, value.State, value.Diagnostic)).ToArray();
        var matrix = UyaContentImportCapabilities.Create("NTSC-U", "1.00", inventory);
        var replacesLevelSettings = selections.TryGetValue("levelSettings", out var levelSettingsSelection)
            && levelSettingsSelection.Mode == ContentImportMode.Replace;
        var replacesMapGroups = selections.TryGetValue("mapGroups", out var mapGroupSelection)
            && mapGroupSelection.Mode == ContentImportMode.Replace;
        var graph = ProjectReferenceGraph.Create(input.Target);
        var groupsByMember = input.Target.Groups
            .SelectMany(group => group.Members.Select(member => (member, group)))
            .GroupBy(value => value.member)
            .ToDictionary(group => group.Key, group => group.Select(value => value.group).ToArray());
        var mapGroupMembers = replacesMapGroups
            ? new HashSet<EntityId>()
            : input.Target.MapGroups.SelectMany(value => value.Groups)
                .SelectMany(value => value.Members).ToHashSet();
        var levelSettingsByEntity = replacesLevelSettings
            ? new Dictionary<EntityId, string[]>()
            : LevelSettingsProtection(input.Target.LevelSettings);
        var sourceByFamily = input.SourceFamilies.ToDictionary(value => value.Family, StringComparer.Ordinal);
        var targetByFamily = TargetItemsByFamily(input.Target);
        var importedGroupIds = ImportedGroupStableIds(input.Target);
        var plans = new List<ContentImportFamilyPlan>(FamilyOrder.Length);

        foreach (var capability in matrix.Families)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = sourceByFamily[capability.Family];
            var target = targetByFamily[capability.Family];
            var selected = selections.TryGetValue(capability.Family, out var selection)
                || input.Selections.Count == 0 && capability.DefaultSelected;
            var mode = selection?.Mode
                ?? (capability.Mode == ContentImportMode.Replace ? ContentImportMode.Replace : ContentImportMode.Add);
            var familyBlockers = new List<string>();
            var familyWarnings = new List<string>();
            if (selected && !Supports(capability.Mode, mode))
                familyBlockers.Add($"{capability.Label} do not support {mode.ToString().ToLowerInvariant()} mode.");
            if (selected && !capability.Selectable)
                familyBlockers.Add(capability.DisabledReason ?? $"{capability.Label} are unavailable.");
            if (selected && capability.Family == "tfrags")
            {
                var targetOwners = input.Target.Tfrags?.Sources.Select(value =>
                    $"source:tfrags:{value.SourceKind.ToString().ToLowerInvariant()}:{value.SourceIndex}")
                    .ToHashSet(StringComparer.Ordinal) ?? [];
                if (!targetOwners.Contains("source:tfrags:primary:0"))
                    familyBlockers.Add("The target level has no primary tfrag owner.");
                else
                {
                    var relocatedOwners = source.Items.Count(value =>
                        value.Key.StartsWith("source:tfrags:chunk:", StringComparison.Ordinal)
                        && !targetOwners.Contains(value.Key));
                    if (relocatedOwners > 0)
                        familyWarnings.Add($"{relocatedOwners} donor streamed tfrag owner(s) will be merged into the target primary terrain.");
                }
            }
            if (selected && capability.Family == "hud" && input.Target.Hud is null)
                familyBlockers.Add("The target level has no HUD storage to replace.");
            if (selected && capability.Family == "fx" && input.Target.Fx is null)
                familyBlockers.Add("The target level has no FX bank to replace.");

            var targetAssetIds = target.Select(value => value.AssetId).OfType<string>().ToHashSet(StringComparer.Ordinal);
            var reuse = source.Items.Select(value => value.AssetId).OfType<string>()
                .Where(targetAssetIds.Contains).Distinct(StringComparer.Ordinal)
                .Select(value => $"asset:{value}").Order(StringComparer.Ordinal).ToArray();
            var conflicts = ClassConflicts(source.Items, target);
            var remap = conflicts.Select(value => $"{value}->allocate:custom").ToArray();
            var replacing = selected && mode == ContentImportMode.Replace;
            var targetIds = target.Select(value => value.Key).ToHashSet(StringComparer.Ordinal);
            var replaceableGroups = replacing
                ? input.Target.Groups.Where(group => importedGroupIds.Contains($"group:{group.GroupId}")
                        && group.Members.All(member => targetIds.Contains($"entity:{member}")))
                    .Select(group => $"group:{group.GroupId}").ToHashSet(StringComparer.Ordinal)
                : [];
            var protection = replacing
                ? target.Where(value => value.Entity is not null).ToDictionary(
                    value => value.Key,
                    value => ProtectionReasons(
                        value.Entity!, targetIds, graph, groupsByMember,
                        mapGroupMembers, levelSettingsByEntity, replaceableGroups),
                    StringComparer.Ordinal)
                : new Dictionary<string, string[]>(StringComparer.Ordinal);
            if (replacing && capability.Family == "tfrags")
                ExpandTfragOwnerProtection(target, protection);
            var protectedIds = protection.Where(value => value.Value.Length > 0)
                .Select(value => value.Key).Order(StringComparer.Ordinal).ToArray();
            if (selected && capability.Family == "skybox" && protectedIds.Length > 0)
                familyBlockers.Add(
                    "Skybox replacement is blocked until every protected target shell can be removed.");
            if (selected && capability.Family == "directionalLights" && protectedIds.Length > 0)
                familyBlockers.Add(
                    "Directional-light replacement is blocked until every protected target light can be removed.");
            var protectedSet = protectedIds.ToHashSet(StringComparer.Ordinal);
            var remove = replacing
                ? target.Select(value => value.Key).Where(value => !protectedSet.Contains(value))
                    .Order(StringComparer.Ordinal).ToArray()
                : [];
            var add = source.Items.Select(value => value.Key).Order(StringComparer.Ordinal).ToArray();
            var projected = target.Count - remove.Length + source.Items.Sum(value => value.ItemCount);
            var capacity = Capacity(capability.Family);
            if (selected && projected > capacity)
                familyBlockers.Add($"{capability.Label} would contain {projected} items, exceeding the {capacity} item limit.");

            plans.Add(new(
                capability.Family,
                capability.Label,
                capability.Mode,
                mode,
                source.State,
                selected,
                capability.DefaultSelected,
                capability.Selectable,
                add,
                reuse,
                remap,
                remove,
                protectedIds,
                capability.Dependencies.Select(value =>
                    $"{capability.Family} requires {value}.").ToArray(),
                conflicts,
                source.EstimatedBytes,
                familyWarnings.Concat(conflicts.Length == 0
                        ? []
                        : new[] { $"{conflicts.Length} class conflict(s) require deterministic allocation." })
                    .Concat(protection.SelectMany(value => value.Value.Select(reason =>
                        $"{value.Key} retained by {reason}.")))
                    .Take(ContentImportPlanSchema.MaximumDiagnostics).ToArray(),
                Limit(familyBlockers)));
        }

        AddOwnedCollisionRemovals(plans, input.Target);
        AddMapGroupDependencyBlockers(plans);
        AddOcclusionDependencyBlockers(plans);

        var warnings = (input.Warnings ?? []).Concat(
                plans.Any(value => value.Selected) ? [] : ["No import families are currently selected."])
            .Distinct(StringComparer.Ordinal).Take(ContentImportPlanSchema.MaximumDiagnostics).ToArray();
        var blockers = plans.Where(value => value.Selected).SelectMany(value => value.Blockers)
            .Distinct(StringComparer.Ordinal).Take(ContentImportPlanSchema.MaximumDiagnostics).ToArray();
        var estimatedBytes = plans.Where(value => value.Selected).Sum(value => value.EstimatedBytes);
        return new(
            ContentImportPlanSchema.CurrentVersion,
            matrix.SchemaVersion,
            BatchId(input, plans),
            "UYA",
            "NTSC-U",
            "1.00",
            input.SourceFingerprint,
            input.SourceLevelHash,
            input.SourceLevel,
            input.TargetProjectId,
            input.TargetFingerprint,
            input.TargetLevel,
            estimatedBytes,
            plans,
            warnings,
            blockers);
    }

    private static void AddOwnedCollisionRemovals(
        IList<ContentImportFamilyPlan> plans,
        ForgeProjectContent target)
    {
        var owners = plans.Where(value => value.Selected
                && value.Mode == ContentImportMode.Replace
                && value.Family is "ties" or "shrubs")
            .SelectMany(value => value.Remove.Select(key => (key, value.Family)))
            .Where(value => value.key.StartsWith("entity:", StringComparison.Ordinal))
            .ToDictionary(value => EntityId.Parse(value.key["entity:".Length..]), value => value.Family);
        if (owners.Count == 0) return;

        foreach (var collision in target.Entities.Where(value =>
                     value.Collision?.LinkedInstanceEntityIds is { Count: > 0 } links
                     && links.All(owners.ContainsKey)))
        {
            var family = collision.Collision!.LinkedInstanceEntityIds!
                .Select(value => owners[value])
                .Order(StringComparer.Ordinal)
                .First();
            var index = plans.ToList().FindIndex(value => value.Family == family);
            var plan = plans[index];
            plans[index] = plan with
            {
                Remove = plan.Remove.Append($"entity:{collision.EntityId}")
                    .Order(StringComparer.Ordinal).ToArray(),
            };
        }
    }

    internal static void ValidateSourceIdentity(
        UyaIsoIdentity identity,
        string expectedFingerprint,
        string targetFingerprint)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!identity.IsSupported
            || identity.Game != "UYA"
            || identity.Region != "NTSC-U"
            || identity.Revision != "1.00"
            || !identity.Fingerprint.Equals(expectedFingerprint, StringComparison.OrdinalIgnoreCase)
            || !identity.Fingerprint.Equals(targetFingerprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The donor ISO does not match the verified UYA NTSC-U 1.00 project source.");
    }

    private static bool Supports(ContentImportMode capability, ContentImportMode requested) =>
        capability == requested || capability == ContentImportMode.AddAndReplace
            && requested is ContentImportMode.Add or ContentImportMode.Replace;

    private static int Capacity(string family) => family switch
    {
        "tfrags" => ProjectTfragSchema.MaximumGroupCount,
        "mapGroups" => ProjectGroupSchema.MaximumGroupCount,
        "directionalLights" => UyaDirectionalLightsContentImportService.MaximumLightCount,
        _ => ushort.MaxValue,
    };

    private static string BatchId(
        UyaContentImportPlanningInput input,
        IReadOnlyList<ContentImportFamilyPlan> plans)
    {
        var selections = string.Join(';', plans.Where(value => value.Selected)
            .OrderBy(value => value.Family, StringComparer.Ordinal)
            .Select(value => $"{value.Family}:{(byte)value.Mode}"));
        var value = $"{ContentImportPlanSchema.CurrentVersion}\n{input.TargetProjectId}\n{input.TargetFingerprint}\n"
            + $"{input.SourceFingerprint}\n{input.SourceLevelHash}\n{input.SourceLevel}\n{selections}";
        return $"import-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..32]}";
    }

    private static string[] Limit(IEnumerable<string> values) => values
        .Distinct(StringComparer.Ordinal).Take(ContentImportPlanSchema.MaximumDiagnostics).ToArray();

    private static void Validate(UyaContentImportPlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProjectPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CatalogRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceIsoPath);
        if (request.SourceFingerprint.Length != 32 || !request.SourceFingerprint.All(Uri.IsHexDigit))
            throw new ArgumentException("The donor source fingerprint must be a 32-character MD5 value.", nameof(request));
        if (request.SourceLevel is < 0 or > 999)
            throw new ArgumentOutOfRangeException(nameof(request), "The donor level is outside the supported range.");
        if (request.Selections.Count > FamilyOrder.Length)
            throw new ArgumentException("The import selection list exceeds the family limit.", nameof(request));
    }

    private sealed record TargetItem(string Key, string? AssetId, int? ClassId, ProjectEntity? Entity);
}
