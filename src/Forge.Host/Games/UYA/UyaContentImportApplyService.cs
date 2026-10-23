using Forge.Host.Domain;

namespace Forge.Host.Games.UYA;

public sealed record UyaContentImportApplyRequest(
    string ProjectPath,
    string CatalogRootPath,
    string SourceIsoPath,
    string SourceFingerprint,
    int SourceLevel,
    string BatchId,
    IReadOnlyList<ContentImportSelection> Selections,
    IReadOnlyList<UyaMobyExternalReferenceChoice>? ReferenceChoices = null);

internal static class UyaContentImportApplyService
{
    private static readonly HashSet<string> StaticFamilies = ["ties", "shrubs"];
    private static readonly HashSet<string> GeometryFamilies = ["cuboids", "spheres", "splines", "paths", "areas"];
    private static readonly HashSet<string> TfragFamily = ["tfrags"];
    private static readonly HashSet<string> MobyFamily = ["mobys"];
    private static readonly HashSet<string> CollisionFamily = ["collision"];
    private static readonly HashSet<string> SkyboxFamily = ["skybox"];
    private static readonly HashSet<string> LevelSettingsFamily = ["levelSettings"];
    private static readonly HashSet<string> DirectionalLightsFamily = ["directionalLights"];
    private static readonly HashSet<string> HudFamily = ["hud"];
    private static readonly HashSet<string> FxFamily = ["fx"];
    private static readonly HashSet<string> OcclusionFamily = ["occlusion"];
    private static readonly HashSet<string> MapGroupsFamily = ["mapGroups"];
    private static readonly HashSet<string> SupportedFamilies =
        [.. StaticFamilies, "tfrags", "mobys", "collision", "mapGroups", "skybox", "levelSettings", "directionalLights", "hud", "fx", "occlusion", .. GeometryFamilies];

    public static async Task<EditorSnapshot> ApplyAsync(
        EditorRuntime runtime,
        UyaContentImportApplyRequest request,
        MobyDexCatalog mobyDex,
        Func<IsoProgress, ValueTask>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(mobyDex);
        var planningRequest = new UyaContentImportPlanRequest(
            request.ProjectPath,
            request.CatalogRootPath,
            request.SourceIsoPath,
            request.SourceFingerprint,
            request.SourceLevel,
            request.Selections);
        var context = await runtime.ReadWorkspaceAsync(request.ProjectPath, async (active, token) =>
            await UyaContentImportPlanningService.PrepareAsync(
                planningRequest, active.CreateProjection(), progress, token), cancellationToken);
        if (!string.Equals(context.Plan.BatchId, request.BatchId, StringComparison.Ordinal))
            throw new InvalidOperationException("The import plan changed; review the refreshed preview before importing.");
        if (context.Plan.Blockers.Count > 0)
            throw new InvalidOperationException($"The import plan is blocked: {string.Join(' ', context.Plan.Blockers)}");
        var referenceChoices = request.ReferenceChoices ?? [];
        ValidateReferenceChoices(context.Plan, referenceChoices);
        var selected = context.Plan.Families.Where(value => value.Selected).ToArray();
        if (selected.Length == 0 || selected.Any(value => !SupportedFamilies.Contains(value.Family)))
            throw new InvalidOperationException("The import contains an unsupported content family.");
        var importedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var commands = new List<ContentImportBatchCommit>();
        var projection = context.Workspace;
        var projectedContent = projection.Content;

        async Task AddAsync(IReadOnlySet<string> families)
        {
            if (!selected.Any(value => families.Contains(value.Family))) return;
            if (!ReferenceEquals(projection.Content, projectedContent))
                projection = projection.CreateProjection(projectedContent);
            var alreadyReplaced = commands.SelectMany(value => value.Batch.Replaced)
                .ToHashSet(StringComparer.Ordinal);
            var plan = Subplan(
                context.Plan, projection.CurrentFingerprint, families, alreadyReplaced);
            ContentImportBatchCommit command;
            if (families.SetEquals(StaticFamilies))
                command = await UyaStaticContentImportService.PrepareCommitAsync(
                    projection, context.Catalog,
                    new(context.Source.Entities, context.Source.OpaqueSections),
                    plan, importedAt, cancellationToken);
            else if (families.SetEquals(GeometryFamilies))
                command = UyaGeometryContentImportService.PrepareCommit(
                    projection, context.Source.Entities, plan, importedAt, cancellationToken,
                    ReferenceChoices(context.Plan, referenceChoices, "areas"));
            else if (families.SetEquals(TfragFamily))
                command = UyaTfragContentImportService.PrepareCommit(
                    projection,
                    new(context.Source.Tfrags, context.Source.BaseLayers, context.Source.TerrainTextures),
                    plan, importedAt, cancellationToken);
            else if (families.SetEquals(MobyFamily))
                command = await UyaMobyContentImportService.PrepareCommitAsync(
                    projection, context.Catalog,
                    new(context.Source.Entities, context.Source.MobyPVars, context.Source.Sounds,
                        context.Source.OpaqueSections),
                    plan, importedAt, mobyDex,
                    ReferenceChoices(context.Plan, referenceChoices, "mobys"), cancellationToken);
            else if (families.SetEquals(CollisionFamily))
                command = await UyaCollisionContentImportService.PrepareCommitAsync(
                    projection, context.Catalog,
                    new(context.Source.Entities, context.Source.BaseLayers),
                    plan, importedAt, cancellationToken);
            else if (families.SetEquals(SkyboxFamily))
                command = await UyaSkyboxContentImportService.PrepareCommitAsync(
                    projection, context.Catalog,
                    new(context.Source.Entities, context.Source.BaseLayers),
                    plan, importedAt, cancellationToken);
            else if (families.SetEquals(LevelSettingsFamily))
                command = await UyaLevelSettingsContentImportService.PrepareCommitAsync(
                    projection, context.Catalog,
                    new(context.Source.LevelSettings, context.Source.Entities),
                    plan,
                    ReferenceChoices(context.Plan, referenceChoices, "levelSettings"),
                    importedAt,
                    cancellationToken);
            else if (families.SetEquals(DirectionalLightsFamily))
                command = await UyaDirectionalLightsContentImportService.PrepareCommitAsync(
                    projection, context.Catalog, context.Source.Entities,
                    plan, importedAt, cancellationToken);
            else if (families.SetEquals(HudFamily))
                command = await UyaHudContentImportService.PrepareCommitAsync(
                    projection, context.Catalog,
                    new(context.Source.Hud, context.Source.OpaqueSections),
                    plan, importedAt, cancellationToken);
            else if (families.SetEquals(FxFamily))
                command = await UyaFxContentImportService.PrepareCommitAsync(
                    projection, context.Catalog, context.Source.Fx,
                    plan, importedAt, cancellationToken);
            else if (families.SetEquals(OcclusionFamily))
                command = UyaOcclusionContentImportService.PrepareCommit(
                    projection, context.Source.Occlusion, plan, importedAt);
            else if (families.SetEquals(MapGroupsFamily))
                command = UyaMapGroupContentImportService.PrepareCommit(
                    projection, context.Source.Entities, context.Source.OpaqueSections,
                    plan, importedAt);
            else
                throw new InvalidOperationException("The import contains an unsupported content-family combination.");
            commands.Add(command);
            projectedContent = command.Content;
        }

        await AddAsync(GeometryFamilies);
        await AddAsync(StaticFamilies);
        await AddAsync(TfragFamily);
        await AddAsync(MobyFamily);
        await AddAsync(CollisionFamily);
        await AddAsync(SkyboxFamily);
        await AddAsync(LevelSettingsFamily);
        await AddAsync(DirectionalLightsFamily);
        await AddAsync(HudFamily);
        await AddAsync(FxFamily);
        await AddAsync(OcclusionFamily);
        await AddAsync(MapGroupsFamily);

        return await runtime.CommitContentImportAsync(
            Compose(context.Plan, projectedContent, commands, importedAt), cancellationToken);
    }

    internal static ContentImportBatchCommit Compose(
        ContentImportPlan plan,
        ForgeProjectContent content,
        IReadOnlyList<ContentImportBatchCommit> commands,
        long importedUnixMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(commands);
        if (commands.Count == 0) throw new InvalidOperationException("The import has no supported changes to commit.");
        if (commands.Any(value => value.Batch.BatchId != plan.BatchId))
            throw new InvalidOperationException("A prepared import batch does not match the approved plan.");
        var selected = plan.Families.Where(value => value.Selected)
            .OrderBy(value => value.Family, StringComparer.Ordinal)
            .ToArray();
        var batch = new ContentImportBatch(
            ContentImportBatchSchema.CurrentVersion,
            plan.BatchId,
            plan.SchemaVersion,
            plan.CapabilitySchemaVersion,
            importedUnixMilliseconds,
            plan.Game,
            plan.Region,
            plan.Revision,
            plan.SourceFingerprint,
            plan.SourceLevelHash,
            plan.SourceLevel,
            selected.Select(value => new ContentImportBatchFamily(value.Family, value.Mode)).ToArray(),
            Merge(commands.Select(value => value.Batch.Added)),
            Merge(commands.Select(value => value.Batch.Reused)),
            Merge(commands.Select(value => value.Batch.Replaced)),
            Merge(commands.Select(value => value.Batch.Protected)));
        return new(
            plan.TargetFingerprint,
            batch,
            content,
            commands.SelectMany(value => value.Attachments).ToArray());
    }

    internal static ContentImportPlan Subplan(
        ContentImportPlan plan,
        string targetFingerprint,
        IReadOnlySet<string> families,
        IReadOnlySet<string> alreadyReplaced)
    {
        var plans = plan.Families.Select(value =>
        {
            var selected = value.Selected && families.Contains(value.Family);
            return value with
            {
                Selected = selected,
                Remove = selected
                    ? value.Remove.Where(item => !alreadyReplaced.Contains(item)).ToArray()
                    : value.Remove,
            };
        }).ToArray();
        return plan with
        {
            TargetFingerprint = targetFingerprint,
            Families = plans,
            Blockers = plans.Where(value => value.Selected).SelectMany(value => value.Blockers)
                .Distinct(StringComparer.Ordinal).ToArray(),
        };
    }

    private static string[] Merge(IEnumerable<IReadOnlyList<string>> values) => values
        .SelectMany(value => value)
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static void ValidateReferenceChoices(
        ContentImportPlan plan,
        IReadOnlyList<UyaMobyExternalReferenceChoice> choices)
    {
        var requirements = plan.ReferenceRequirements ?? [];
        var requirementKeys = requirements.Select(Key).ToArray();
        var choiceKeys = choices.Select(Key).ToArray();
        if (requirementKeys.Distinct().Count() != requirementKeys.Length
            || choiceKeys.Distinct().Count() != choiceKeys.Length
            || !requirementKeys.Order().SequenceEqual(choiceKeys.Order()))
            throw new InvalidDataException("Import reference choices do not match the approved plan.");
    }

    private static UyaMobyExternalReferenceChoice[] ReferenceChoices(
        ContentImportPlan plan,
        IReadOnlyList<UyaMobyExternalReferenceChoice> choices,
        string family)
    {
        var keys = (plan.ReferenceRequirements ?? []).Where(value => value.Family == family)
            .Select(Key).ToHashSet();
        return choices.Where(value => keys.Contains(Key(value))).ToArray();
    }

    private static (int OwnerSourceIndex, string FieldKey, int SourceValue) Key(
        ContentImportReferenceRequirement value) =>
        (value.OwnerSourceIndex, value.FieldKey, value.SourceValue);

    private static (int OwnerSourceIndex, string FieldKey, int SourceValue) Key(
        UyaMobyExternalReferenceChoice value) =>
        (value.OwnerSourceIndex, value.FieldKey, value.SourceValue);
}
