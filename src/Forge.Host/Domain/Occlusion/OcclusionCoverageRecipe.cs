using System.Security.Cryptography;
using System.Text;

namespace Forge.Host.Domain.Occlusion;

public static class OcclusionCoverageRecipe
{
    public static void Validate(ProjectOcclusionCoverageState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.SchemaVersion != ProjectOcclusionCoverageSchema.CurrentVersion
            || state.Sources is null || state.Sources.Count > ProjectOcclusionCoverageSchema.MaximumSources)
            throw new InvalidDataException("Occlusion coverage schema or source count is invalid.");
        var ids = new HashSet<EntityId>();
        var markers = 0;
        foreach (var source in state.Sources)
        {
            if (source is null) throw new InvalidDataException("Occlusion coverage sources cannot be null.");
            Identifier(source.Id, ids);
            ForgeProjectValidation.ValidateText(source.Name, "Occlusion coverage name");
            if (!Enum.IsDefined(source.Role)) throw new InvalidDataException("Occlusion coverage role is invalid.");
            if (source.Transform is null) throw new InvalidDataException("Occlusion coverage transform is required.");
            ForgeProjectValidation.ValidateTransform(source.Transform);
            Position(source.Transform.Position);
            var q = source.Transform.Rotation;
            var norm = (double)q.X * q.X + (double)q.Y * q.Y + (double)q.Z * q.Z + (double)q.W * q.W;
            if (Math.Abs(norm - 1) > 0.0001 || source.Transform.Scale != new ProjectVector3(1, 1, 1))
                throw new InvalidDataException("Occlusion coverage needs a unit rotation and unit transform scale; edit its shape dimensions instead.");
            if ((source.BoxSize is null ? 0 : 1) + (source.Cylinder is null ? 0 : 1)
                + (source.Graph?.Size is null ? 0 : 1) != 1)
                throw new InvalidDataException("Occlusion coverage must contain exactly one box or cylinder boundary.");
            if (source.BoxSize is { } box) Size(box);
            if (source.Cylinder is { } cylinder && (!Dimension(cylinder.Radius) || !Dimension(cylinder.Height)))
                throw new InvalidDataException("Occlusion cylinder radius and height must be positive, finite and bounded.");
            if (source.Graph is not { } graph) continue;
            if (graph.Size is { } legacySize) { Size(legacySize); if (graph.Disabled) throw new InvalidDataException("Legacy graph bounds must be converted to a box before disabling the graph."); }
            if (graph.StickyDistance is { } distance && (!float.IsFinite(distance) || distance < 0
                || distance > ProjectOcclusionCoverageSchema.MaximumExtent))
                throw new InvalidDataException("Sticky distance must be finite and between 0 and 1,000,000 map units.");
            if (source.Role != ProjectOcclusionCoverageRole.Include && !graph.Disabled || graph.Markers is null
                || graph.Markers.Count > ProjectOcclusionCoverageSchema.MaximumMarkersPerGraph
                || (markers += graph.Markers.Count) > ProjectOcclusionCoverageSchema.MaximumMarkers)
                throw new InvalidDataException("Occlusion graphs must include coverage and have bounded marker lists.");
            foreach (var marker in graph.Markers)
            {
                if (marker is null) throw new InvalidDataException("Occlusion graph markers cannot be null.");
                Identifier(marker.Id, ids);
                ForgeProjectValidation.ValidateText(marker.Name, "Occlusion marker name");
                Position(marker.Position);
                if (!Contains(source, marker.Position))
                    throw new InvalidDataException("Occlusion graph markers must be inside their local graph region.");
            }
        }
    }

    public static string Fingerprint(ProjectOcclusionCoverageState state)
    {
        Validate(state);
        var sources = state.Sources.Where(source => source.Enabled).Select(source =>
            Encoding.UTF8.GetString(ForgeProjectPersistence.Serialize(new
            {
                source.Role,
                source.Transform,
                BoxSize = HasGraph(source) ? null : source.BoxSize,
                source.Cylinder,
                Graph = HasGraph(source) ? GraphIdentity(source.Graph!, Dimensions(source)) : null,
            }))).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return Convert.ToHexStringLower(SHA256.HashData(ForgeProjectPersistence.Serialize(new
        {
            state.SchemaVersion,
            state.PrimaryEnabled,
            Sources = sources,
        })));
    }

    public static bool HasGraph(ProjectOcclusionCoverageSource source) => source.Graph is { Disabled: false };

    public static ProjectVector3 Dimensions(ProjectOcclusionCoverageSource source) => source.BoxSize
        ?? (source.Cylinder is { } cylinder ? new(cylinder.Radius * 2, cylinder.Radius * 2, cylinder.Height) : source.Graph!.Size!);

    public static bool Contains(ProjectOcclusionCoverageSource source, ProjectVector3 position)
    {
        var size = Dimensions(source);
        return Math.Abs(position.Z) <= size.Z / 2 && (source.Cylinder is { } cylinder
            ? (double)position.X * position.X + (double)position.Y * position.Y <= (double)cylinder.Radius * cylinder.Radius
            : Math.Abs(position.X) <= size.X / 2 && Math.Abs(position.Y) <= size.Y / 2);
    }

    private static object GraphIdentity(ProjectOcclusionCoverageGraph graph, ProjectVector3 size)
    {
        var markers = graph.Markers.Select(marker => marker.Position).Distinct()
            .OrderBy(p => p.X).ThenBy(p => p.Y).ThenBy(p => p.Z).ToArray();
        return graph.Sticky ? new { Size = size, Markers = markers, StickyDistance = graph.StickyDistance ?? 12 }
            : new { Size = size, Markers = markers };
    }

    internal static ProjectOcclusionCoverageState Copy(ProjectOcclusionCoverageState state)
    {
        Validate(state);
        return state with
        {
            Sources = state.Sources.Select(source => source.Graph is { } graph
                ? source with { Graph = graph with { Markers = graph.Markers.ToArray() } }
                : source).ToArray(),
        };
    }

    private static void Identifier(EntityId id, HashSet<EntityId> ids)
    {
        if (id.Value == Guid.Empty || !ids.Add(id))
            throw new InvalidDataException("Occlusion source and marker IDs must be nonempty and unique within the recipe.");
    }

    private static void Position(ProjectVector3? value)
    {
        if (value is null || new[] { value.X, value.Y, value.Z }.Any(v =>
                !float.IsFinite(v) || Math.Abs(v) > ProjectOcclusionCoverageSchema.MaximumExtent))
            throw new InvalidDataException("Occlusion positions must be finite and bounded.");
    }

    private static void Size(ProjectVector3? value)
    {
        if (value is null || !Dimension(value.X) || !Dimension(value.Y) || !Dimension(value.Z))
            throw new InvalidDataException("Occlusion region dimensions must be positive, finite and bounded.");
    }

    private static bool Dimension(float value) => float.IsFinite(value)
        && value > 0 && value <= ProjectOcclusionCoverageSchema.MaximumExtent;
}
