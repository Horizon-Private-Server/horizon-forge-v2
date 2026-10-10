namespace Forge.Host.Domain;

public static class ProjectMobyPVarSchema
{
    public const int CurrentVersion = 2;
    public const int MaximumEntries = 65_536;
}

public sealed record ProjectMobyPVarState(
    int SchemaVersion,
    int SourceTableEntryCount,
    IReadOnlyList<ProjectMobyPVar> Entries);

public sealed record ProjectMobyPVar(
    EntityId EntityId,
    int? SourceTableIndex,
    byte[] Data,
    byte[] BaselineData,
    IReadOnlyList<int> MobyLinkOffsets,
    IReadOnlyList<int> RelativePointerOffsets,
    IReadOnlyList<ProjectMobyPVarReference> References);

public sealed record ProjectMobyPVarReference(
    int Offset,
    int Length,
    long NullValue,
    int SourceValue,
    ProjectReference Reference);
