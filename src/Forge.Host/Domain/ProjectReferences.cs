namespace Forge.Host.Domain;

public sealed class ProjectReferenceGraph
{
    private readonly IReadOnlyDictionary<EntityId, ProjectReferenceEdge[]> _outgoing;
    private readonly IReadOnlyDictionary<EntityId, ProjectReferenceEdge[]> _incoming;

    private ProjectReferenceGraph(
        IReadOnlyDictionary<EntityId, ProjectReferenceEdge[]> outgoing,
        IReadOnlyDictionary<EntityId, ProjectReferenceEdge[]> incoming)
    {
        _outgoing = outgoing;
        _incoming = incoming;
    }

    public static ProjectReferenceGraph Create(ForgeProjectContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var entityKinds = content.Entities.ToDictionary(entity => entity.EntityId, ProjectReferences.KindOf);
        var edges = content.Entities.SelectMany(ProjectReferences.FromEntity)
            .Select(edge => edge.Reference is
                    { Domain: ProjectReferenceDomain.Entity, EntityId: { } target }
                    && entityKinds.TryGetValue(target, out var targetKind)
                ? edge with { Reference = edge.Reference with { EntityKind = targetKind } }
                : edge)
            .ToArray();
        return new(
            edges.GroupBy(edge => edge.OwnerEntityId)
                .ToDictionary(group => group.Key, group => group.ToArray()),
            edges.Where(edge => edge.Reference is { Domain: ProjectReferenceDomain.Entity, EntityId: not null })
                .GroupBy(edge => edge.Reference.EntityId!.Value)
                .ToDictionary(group => group.Key, group => group.ToArray()));
    }

    public IReadOnlyList<ProjectReferenceEdge> Outgoing(EntityId ownerEntityId) =>
        _outgoing.GetValueOrDefault(ownerEntityId) ?? [];

    public IReadOnlyList<ProjectReferenceEdge> Incoming(EntityId targetEntityId) =>
        _incoming.GetValueOrDefault(targetEntityId) ?? [];
}

internal static class ProjectReferences
{
    public const string EntityAsset = "entity.asset";
    public const string AreaSplines = "geometry.area.splines";
    public const string AreaCuboids = "geometry.area.cuboids";
    public const string AreaSpheres = "geometry.area.spheres";
    public const string AreaCylinders = "geometry.area.cylinders";
    public const string AreaNegativeCuboids = "geometry.area.negativeCuboids";
    public const string CollisionAttachment = "collision.attachment";

    public static IEnumerable<ProjectReferenceEdge> FromEntity(ProjectEntity entity)
    {
        if (entity.Asset is { } asset)
            yield return new(entity.EntityId, new(
                ProjectReferenceDomain.Asset, EntityAsset, true,
                AssetKind: asset.Kind, AssetId: asset.Id));
        if (entity.Geometry?.Area is { } area)
        {
            foreach (var edge in AreaEdges(entity.EntityId, area.Splines)) yield return edge;
            foreach (var edge in AreaEdges(entity.EntityId, area.Cuboids)) yield return edge;
            foreach (var edge in AreaEdges(entity.EntityId, area.Spheres)) yield return edge;
            foreach (var edge in AreaEdges(entity.EntityId, area.Cylinders)) yield return edge;
            foreach (var edge in AreaEdges(entity.EntityId, area.NegativeCuboids)) yield return edge;
        }
        if (entity.Collision?.Attachment is { } attachment)
            yield return new(entity.EntityId, new(
                ProjectReferenceDomain.Entity, CollisionAttachment, false,
                EntityKind: ProjectEntityKind.Entity, EntityId: attachment.ParentEntityId));
    }

    public static ProjectEntityKind KindOf(ProjectEntity entity) => entity switch
    {
        { Geometry.Cuboid: not null } => ProjectEntityKind.Cuboid,
        { Geometry.Sphere: not null } => ProjectEntityKind.Sphere,
        { Geometry.Cylinder: not null } => ProjectEntityKind.Cylinder,
        { Geometry.Pill: not null } => ProjectEntityKind.Pill,
        { Geometry.Spline: not null } => ProjectEntityKind.Spline,
        { Geometry.GrindPath: not null } => ProjectEntityKind.GrindPath,
        { Geometry.Area: not null } => ProjectEntityKind.Area,
        { Collision: not null } => ProjectEntityKind.Collision,
        { SkyShell: not null } => ProjectEntityKind.SkyShell,
        { Lighting.DirectionalLight: not null } => ProjectEntityKind.DirectionalLight,
        { Lighting.PointLight: not null } => ProjectEntityKind.PointLight,
        { Lighting.EnvironmentSamplePoint: not null } => ProjectEntityKind.EnvironmentSample,
        { Lighting.EnvironmentTransition: not null } => ProjectEntityKind.EnvironmentTransition,
        { Camera: not null } => ProjectEntityKind.Camera,
        { AmbientSound: not null } => ProjectEntityKind.AmbientSound,
        { Asset.Kind: AssetKind.Moby } => ProjectEntityKind.Moby,
        { Asset.Kind: AssetKind.Tie } => ProjectEntityKind.Tie,
        { Asset.Kind: AssetKind.Shrub } => ProjectEntityKind.Shrub,
        { Asset.Kind: AssetKind.Tfrag } => ProjectEntityKind.Tfrag,
        _ => ProjectEntityKind.Entity,
    };

    public static ProjectReference ImportedEntityReference(
        string fieldKey,
        ProjectEntityKind targetKind,
        bool nullable,
        int sourceIndex,
        IReadOnlyDictionary<int, EntityId> entities) => new(
            ProjectReferenceDomain.Entity,
            fieldKey,
            nullable,
            EntityKind: targetKind,
            EntityId: entities.TryGetValue(sourceIndex, out var entityId) ? entityId : null);

    public static int ResolveNativeIndex(
        ProjectReferenceEdge edge,
        IReadOnlyDictionary<EntityId, int> indexes)
    {
        ArgumentNullException.ThrowIfNull(edge);
        ArgumentNullException.ThrowIfNull(indexes);
        if (edge.Reference is not { Domain: ProjectReferenceDomain.Entity, EntityId: { } target }
            || !indexes.TryGetValue(target, out var index))
            throw new InvalidDataException(
                $"Entity {edge.OwnerEntityId} field {edge.Reference.FieldKey} raw value "
                + $"{edge.SourceValue?.ToString() ?? "null"} cannot resolve target "
                + $"{edge.Reference.EntityId?.ToString() ?? "null"}.");
        return index;
    }

    public static ForgeProjectContent Migrate(ForgeProjectContent content)
    {
        var entities = content.Entities.Select(entity => entity.Geometry?.Area is not { } area
            ? entity
            : entity with
            {
                Geometry = entity.Geometry with
                {
                    Area = area with
                    {
                        Splines = Migrate(area.Splines, AreaSplines, ProjectEntityKind.Spline),
                        Cuboids = Migrate(area.Cuboids, AreaCuboids, ProjectEntityKind.Cuboid),
                        Spheres = Migrate(area.Spheres, AreaSpheres, ProjectEntityKind.Sphere),
                        Cylinders = Migrate(area.Cylinders, AreaCylinders, ProjectEntityKind.Cylinder),
                        NegativeCuboids = Migrate(
                            area.NegativeCuboids, AreaNegativeCuboids, ProjectEntityKind.Cuboid),
                    },
                },
            }).ToArray();
        return content with { Entities = entities };
    }

    public static ProjectEntity ClearNullableEntityReferences(ProjectEntity entity, IReadOnlySet<EntityId> targets)
    {
        if (entity.Geometry?.Area is not { } area) return entity;
        return entity with
        {
            Geometry = entity.Geometry with
            {
                Area = area with
                {
                    Splines = Clear(area.Splines, targets),
                    Cuboids = Clear(area.Cuboids, targets),
                    Spheres = Clear(area.Spheres, targets),
                    Cylinders = Clear(area.Cylinders, targets),
                    NegativeCuboids = Clear(area.NegativeCuboids, targets),
                },
            },
        };
    }

    public static ProjectEntity UpdateEntityReference(
        ProjectEntity entity,
        string fieldKey,
        int? sourceValue,
        EntityId? targetEntityId)
    {
        if (fieldKey == CollisionAttachment && sourceValue is null
            && entity.Collision?.Attachment is { } attachment)
        {
            if (targetEntityId is null)
                throw new InvalidOperationException($"Entity field {fieldKey} is required.");
            return entity with
            {
                Collision = entity.Collision with
                {
                    Attachment = attachment with { ParentEntityId = targetEntityId.Value },
                },
            };
        }
        if (sourceValue is not { } sourceIndex || entity.Geometry?.Area is not { } area)
            throw new InvalidOperationException($"Entity has no editable reference field {fieldKey}.");
        var geometry = entity.Geometry;
        return entity with
        {
            Geometry = geometry with
            {
                Area = fieldKey switch
                {
                    AreaSplines => area with { Splines = Update(area.Splines, sourceIndex, targetEntityId) },
                    AreaCuboids => area with { Cuboids = Update(area.Cuboids, sourceIndex, targetEntityId) },
                    AreaSpheres => area with { Spheres = Update(area.Spheres, sourceIndex, targetEntityId) },
                    AreaCylinders => area with { Cylinders = Update(area.Cylinders, sourceIndex, targetEntityId) },
                    AreaNegativeCuboids => area with
                    {
                        NegativeCuboids = Update(area.NegativeCuboids, sourceIndex, targetEntityId),
                    },
                    _ => throw new InvalidOperationException($"Entity has no editable reference field {fieldKey}."),
                },
            },
        };
    }

    private static IEnumerable<ProjectReferenceEdge> AreaEdges(
        EntityId ownerEntityId,
        IEnumerable<ProjectGeometryLink> links) =>
        links.Select(link => new ProjectReferenceEdge(
            ownerEntityId,
            link.Reference ?? throw new InvalidDataException(
                $"Entity {ownerEntityId} has an unmigrated area reference at source index {link.SourceIndex}."),
            link.SourceIndex));

    private static ProjectGeometryLink[] Migrate(
        IEnumerable<ProjectGeometryLink> links,
        string fieldKey,
        ProjectEntityKind targetKind) => links.Select(link => link.Reference is not null
        ? link with { EntityId = null }
        : link with
        {
            EntityId = null,
            Reference = new(
                ProjectReferenceDomain.Entity, fieldKey, true,
                EntityKind: targetKind, EntityId: link.EntityId),
        }).ToArray();

    private static ProjectGeometryLink[] Clear(
        IEnumerable<ProjectGeometryLink> links,
        IReadOnlySet<EntityId> targets) => links
        .Where(link => link.Reference?.EntityId is not { } id || !targets.Contains(id))
        .ToArray();

    private static ProjectGeometryLink[] Update(
        IReadOnlyList<ProjectGeometryLink> links,
        int sourceIndex,
        EntityId? targetEntityId)
    {
        var index = links.ToList().FindIndex(link => link.SourceIndex == sourceIndex);
        if (index < 0) throw new InvalidOperationException($"Reference source value {sourceIndex} was not found.");
        var values = links.ToArray();
        values[index] = values[index] with
        {
            Reference = values[index].Reference! with { EntityId = targetEntityId },
        };
        return values;
    }
}
