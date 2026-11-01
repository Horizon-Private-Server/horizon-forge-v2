using System.Text.Json.Serialization;

namespace Forge.Host.Domain;

public static class ProjectOcclusionCoverageSchema
{
    public const int CurrentVersion = 1;
    public const int MaximumSources = 1_024;
    public const int MaximumMarkersPerGraph = 4_096;
    public const int MaximumMarkers = 16_384;
    public const float MaximumExtent = 1_000_000;
}

public enum ProjectOcclusionCoverageRole { Include = 1, Exclude = 2 }

public sealed record ProjectOcclusionCoverageState(
    int SchemaVersion,
    bool PrimaryEnabled,
    IReadOnlyList<ProjectOcclusionCoverageSource> Sources)
{
    public static ProjectOcclusionCoverageState PrimaryOnly { get; } = new(
        ProjectOcclusionCoverageSchema.CurrentVersion, true, []);

    [JsonIgnore]
    public bool RequiresGeneration => !PrimaryEnabled || Sources.Any(source => source.Enabled);
}

public sealed record ProjectOcclusionCoverageSource(
    EntityId Id,
    string Name,
    bool Enabled,
    ProjectOcclusionCoverageRole Role,
    ProjectTransform Transform,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ProjectVector3? BoxSize = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ProjectOcclusionCoverageCylinder? Cylinder = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ProjectOcclusionCoverageGraph? Graph = null);

public sealed record ProjectOcclusionCoverageCylinder(float Radius, float Height);

public sealed record ProjectOcclusionCoverageGraph(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ProjectVector3? Size,
    IReadOnlyList<ProjectOcclusionCoverageMarker> Markers,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Sticky = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? StickyDistance = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Disabled = false);

public sealed record ProjectOcclusionCoverageMarker(EntityId Id, string Name, ProjectVector3 Position);
