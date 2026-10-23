using Forge.Host.Domain;

namespace Forge.Host.Games.UYA;

internal static class UyaContentImportValidation
{
    public static void ValidatePlan(ForgeProjectWorkspace workspace, ContentImportPlan plan)
    {
        if (!string.Equals(plan.TargetFingerprint, workspace.CurrentFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("The import plan target no longer matches the active project.");
        if (plan.Blockers.Count > 0)
            throw new InvalidOperationException($"The import plan is blocked: {string.Join(' ', plan.Blockers)}");
    }

    public static ContentImportFamilyPlan RequireSingleFamily(
        ForgeProjectWorkspace workspace,
        ContentImportPlan plan,
        string familyName,
        ContentImportMode supportedMode,
        string errorMessage)
    {
        ValidatePlan(workspace, plan);
        var family = plan.Families.SingleOrDefault(value => value.Selected);
        if (family is null
            || family.Family != familyName
            || !family.Selectable
            || !Supports(supportedMode, family.Mode))
            throw new InvalidOperationException(errorMessage);
        return family;
    }

    private static bool Supports(ContentImportMode supported, ContentImportMode requested) =>
        supported == requested || supported == ContentImportMode.AddAndReplace
            && requested is ContentImportMode.Add or ContentImportMode.Replace;
}
