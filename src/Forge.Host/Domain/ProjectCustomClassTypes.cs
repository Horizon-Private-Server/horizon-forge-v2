namespace Forge.Host.Domain;

public static class ProjectCustomClassSchema
{
    public const int CurrentVersion = 1;
    public const int CurrentAllocationVersion = 1;
    public const int MaximumAllocations = ushort.MaxValue + 1;
}

public enum ProjectCustomClassOrigin
{
    ImportedModel,
    DonorMap,
}

public sealed record ProjectCustomClassProvenance(
    ProjectCustomClassOrigin Origin,
    int? SourceClassId = null);

public sealed record ProjectCustomClassAllocation(
    ProjectAssetReference Asset,
    int ClassId,
    ProjectCustomClassProvenance Provenance);

public sealed record ProjectCustomClassRegistry(
    int SchemaVersion,
    int AllocationVersion,
    IReadOnlyList<ProjectCustomClassAllocation> Allocations)
{
    public static ProjectCustomClassRegistry Empty { get; } = new(
        ProjectCustomClassSchema.CurrentVersion,
        ProjectCustomClassSchema.CurrentAllocationVersion,
        []);
}
