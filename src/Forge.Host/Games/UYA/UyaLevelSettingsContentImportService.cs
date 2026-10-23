using Forge.Host.Domain;

namespace Forge.Host.Games.UYA;

internal sealed record UyaLevelSettingsContentImportSource(
    ProjectLevelSettings? Settings,
    IReadOnlyList<ProjectEntity> Entities);

internal static class UyaLevelSettingsContentImportService
{
    internal static async Task<ContentImportBatchCommit> PrepareCommitAsync(
        ForgeProjectWorkspace workspace,
        AssetCatalogStore catalog,
        UyaLevelSettingsContentImportSource source,
        ContentImportPlan plan,
        IReadOnlyList<UyaMobyExternalReferenceChoice>? referenceChoices = null,
        long? importedUnixMilliseconds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(plan);
        var family = UyaContentImportValidation.RequireSingleFamily(
            workspace, plan, "levelSettings", ContentImportMode.Replace,
            "This importer accepts one replace-only level-settings family.");
        var expectedRemove = workspace.Content.LevelSettings is null ? [] : new[] { "settings:project" };
        if (!family.Remove.SequenceEqual(expectedRemove, StringComparer.Ordinal) || family.Protect.Count > 0)
            throw new InvalidOperationException("Level-settings replacement does not match the active project singleton.");

        var donor = source.Settings
            ?? throw new InvalidDataException("The donor level settings are missing.");
        var choices = referenceChoices ?? [];
        var requirements = (plan.ReferenceRequirements ?? [])
            .Where(value => value.Family == "levelSettings").ToArray();
        if (choices.Count != requirements.Length
            || choices.Select(Key).Distinct().Count() != choices.Count
            || choices.Any(choice => !requirements.Any(requirement => Key(requirement) == Key(choice))))
            throw new InvalidDataException("Level-settings reference choices do not match the approved plan.");
        var choiceByKey = choices.ToDictionary(Key);
        var scene = donor.Scene;
        var settings = scene is null ? donor : donor with
        {
            Scene = scene with
            {
                ShipPath = Rebind(scene.ShipPath, ProjectEntityKind.Spline, "splines"),
                ShipCameraCuboidStart = Rebind(
                    scene.ShipCameraCuboidStart, ProjectEntityKind.Cuboid, "cuboids"),
                ShipCameraCuboidEnd = Rebind(
                    scene.ShipCameraCuboidEnd, ProjectEntityKind.Cuboid, "cuboids"),
            },
        };
        var content = workspace.Content with { LevelSettings = settings };
        await UyaBaseLayerStore.ValidateLevelSettingsAsync(
            workspace.CreateProjection(content), catalog, cancellationToken);

        var replacing = workspace.Content.LevelSettings is not null;
        var batch = new ContentImportBatch(
            ContentImportBatchSchema.CurrentVersion,
            plan.BatchId,
            plan.SchemaVersion,
            plan.CapabilitySchemaVersion,
            importedUnixMilliseconds ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            plan.Game,
            plan.Region,
            plan.Revision,
            plan.SourceFingerprint,
            plan.SourceLevelHash,
            plan.SourceLevel,
            [new("levelSettings", ContentImportMode.Replace)],
            replacing ? [] : ["settings:project"],
            [],
            replacing ? ["settings:project"] : [],
            []);
        return new(plan.TargetFingerprint, batch, content, []);

        ProjectIndexedReference Rebind(
            ProjectIndexedReference value,
            ProjectEntityKind kind,
            string importedFamily)
        {
            if (value.Reference is not
                { Domain: ProjectReferenceDomain.Entity, Nullable: true, EntityKind: var actualKind }
                || actualKind != kind || value.SourceValue < -1)
                throw new InvalidDataException($"Donor level setting {value.Reference?.FieldKey} is invalid.");
            if (value.SourceValue == -1)
                return value with { Reference = value.Reference with { EntityId = null } };

            var donorEntity = value.Reference.EntityId is { } donorId
                ? source.Entities.SingleOrDefault(candidate => candidate.EntityId == donorId)
                : null;
            if (donorEntity is not null
                && donorEntity.Provenance?.SourceIndex == value.SourceValue
                && ProjectReferences.KindOf(donorEntity) == kind)
            {
                var importedId = UyaGeometryContentImportService.ImportedEntityId(
                    plan.BatchId, importedFamily, value.SourceValue);
                var imported = workspace.Content.Entities.SingleOrDefault(candidate => candidate.EntityId == importedId);
                if (imported is not null && ProjectReferences.KindOf(imported) == kind)
                    return value with { Reference = value.Reference with { EntityId = importedId } };
            }

            var key = (-1, value.Reference.FieldKey, value.SourceValue);
            var requirement = requirements.SingleOrDefault(candidate => Key(candidate) == key)
                ?? throw new InvalidDataException(
                    $"Donor level setting {value.Reference.FieldKey} has no approved reference mapping.");
            var choice = choiceByKey[key];
            if (!Enum.IsDefined(choice.Action))
                throw new InvalidDataException("A level-settings reference choice has an invalid action.");
            if (choice.Action == UyaMobyExternalReferenceAction.Clear)
            {
                if (choice.TargetEntityId is not null)
                    throw new InvalidDataException(
                        $"Level setting {value.Reference.FieldKey} has an invalid clear choice.");
                return value with
                {
                    SourceValue = -1,
                    Reference = value.Reference with { EntityId = null },
                };
            }
            if (choice.TargetEntityId is not { } targetId
                || !requirement.CandidateEntityIds.Contains(targetId)
                || workspace.Content.Entities.SingleOrDefault(candidate => candidate.EntityId == targetId) is not { } target
                || ProjectReferences.KindOf(target) != kind)
                throw new InvalidDataException(
                    $"Level setting {value.Reference.FieldKey} maps to a missing or incompatible target entity.");
            return value with { Reference = value.Reference with { EntityId = targetId } };
        }
    }

    private static (int OwnerSourceIndex, string FieldKey, int SourceValue) Key(
        UyaMobyExternalReferenceChoice value) =>
        (value.OwnerSourceIndex, value.FieldKey, value.SourceValue);

    private static (int OwnerSourceIndex, string FieldKey, int SourceValue) Key(
        ContentImportReferenceRequirement value) =>
        (value.OwnerSourceIndex, value.FieldKey, value.SourceValue);
}
