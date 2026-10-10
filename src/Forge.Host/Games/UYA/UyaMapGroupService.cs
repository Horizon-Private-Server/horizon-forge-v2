using Forge.Host.Domain;
using RatchetPs2.Games.UYA.Gameplay;

namespace Forge.Host.Games.UYA;

internal static class UyaMapGroupService
{
    private const long MaximumGroupBlockBytes = 2_100_000;

    private static readonly (EditorMapGroupKind Kind, string OpaqueSection, string MemberSection)[] Sections =
    [
        (EditorMapGroupKind.Moby, "gameplay/moby_groups", "gameplay/core/moby_instances"),
        (EditorMapGroupKind.Tie, "gameplay/tie_groups", "gameplay/core/tie_instances"),
        (EditorMapGroupKind.Shrub, "gameplay/shrub_groups", "gameplay/core/shrub_instances"),
    ];

    public static async Task<EditorMapGroupCatalog> ReadAsync(
        ForgeProjectWorkspace workspace,
        CancellationToken cancellationToken)
    {
        var inspection = await OpaqueContentStore.InspectAsync(workspace.RootPath, cancellationToken);
        if (!inspection.IsValid)
            return new([], [new("map-group.source-unavailable", EditorDiagnosticSeverity.Warning,
                $"Map groups could not be read: {string.Join(' ', inspection.Blockers)}")]);

        var groups = new List<EditorMapGroupSource>();
        var diagnostics = new List<EditorDiagnostic>();
        var opaqueRoot = ForgeProjectPersistence.ResolveRelativePath(
            workspace.RootPath, OpaqueContentStore.RelativeRootPath);
        foreach (var (kind, opaqueSection, memberSection) in Sections)
        {
            var matchingSections = inspection.Manifest!.Sections
                .Where(value => value.Name == opaqueSection).Take(2).ToArray();
            if (matchingSections.Length == 0) continue;
            if (matchingSections.Length > 1)
            {
                diagnostics.Add(new("map-group.invalid-source", EditorDiagnosticSeverity.Warning,
                    $"{kind} map groups could not be read (duplicate opaque sections)."));
                continue;
            }
            var section = matchingSections[0];
            try
            {
                if (section.Size > MaximumGroupBlockBytes)
                    throw new InvalidDataException("group block exceeds the supported membership limit");
                var bytes = await File.ReadAllBytesAsync(
                    ForgeProjectPersistence.ResolveRelativePath(opaqueRoot, section.Blob), cancellationToken);
                var parsed = UyaTieGroupsReader.Read(bytes).Groups;
                if (groups.Count + parsed.Count > ProjectGroupSchema.MaximumGroupCount
                    || parsed.Any(group => group.Length > ProjectGroupSchema.MaximumMembersPerGroup)
                    || groups.Sum(group => (long)group.MemberSourceIndices.Count)
                        + parsed.Sum(group => (long)group.Length) > ProjectGroupSchema.MaximumTotalMemberships)
                    throw new InvalidDataException("group block exceeds the supported group or membership limit");
                groups.AddRange(parsed.Select((members, index) =>
                    new EditorMapGroupSource(kind, index, memberSection, members)));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or InvalidDataException or OverflowException)
            {
                diagnostics.Add(new("map-group.invalid-source", EditorDiagnosticSeverity.Warning,
                    $"{kind} map groups could not be read ({exception.Message})."));
            }
        }
        return new(groups, diagnostics);
    }
}
