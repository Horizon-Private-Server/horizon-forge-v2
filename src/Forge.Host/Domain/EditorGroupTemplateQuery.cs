namespace Forge.Host.Domain;

public sealed class EditorGroupTemplateQuery
{
    private const float CellSize = 32f;
    private const float MaximumQueryRadius = 1_024f;
    private readonly IReadOnlyDictionary<EntityId, ProjectEntity> _entities;
    private readonly IReadOnlyDictionary<ProjectEntityKind, ProjectEntity[]> _entitiesByKind;
    private readonly IReadOnlyDictionary<(long X, long Y, long Z), ProjectEntity[]> _spatialCells;
    private readonly IReadOnlyDictionary<EntityId, ProjectEntity[]> _linkedCollision;
    private readonly ProjectReferenceGraph _references;

    public EditorGroupTemplateQuery(ForgeProjectContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        _entities = content.Entities.ToDictionary(entity => entity.EntityId);
        _entitiesByKind = content.Entities
            .GroupBy(ProjectReferences.KindOf)
            .ToDictionary(group => group.Key, group => Sort(group));
        _spatialCells = content.Entities
            .GroupBy(entity => Cell(entity.Transform.Position))
            .ToDictionary(group => group.Key, group => Sort(group));
        _linkedCollision = content.Entities
            .Where(entity => entity.Collision?.LinkedInstanceEntityIds is { Count: > 0 })
            .SelectMany(entity => entity.Collision!.LinkedInstanceEntityIds!
                .Select(instanceId => (instanceId, entity)))
            .GroupBy(value => value.instanceId)
            .ToDictionary(group => group.Key, group => Sort(group.Select(value => value.entity)));
        _references = ProjectReferenceGraph.Create(content);
    }

    public IReadOnlyList<ProjectEntity> OfKind(ProjectEntityKind kind) =>
        _entitiesByKind.GetValueOrDefault(kind) ?? [];

    public ProjectEntity? Find(EntityId entityId) => _entities.GetValueOrDefault(entityId);

    public IReadOnlyList<ProjectEntity> LinkedCollision(EntityId instanceEntityId) =>
        _linkedCollision.GetValueOrDefault(instanceEntityId) ?? [];

    public IReadOnlyList<ProjectReferenceEdge> Outgoing(EntityId ownerEntityId, string? fieldKey = null) =>
        FilterReferences(_references.Outgoing(ownerEntityId), fieldKey);

    public IReadOnlyList<ProjectReferenceEdge> Incoming(EntityId targetEntityId, string? fieldKey = null) =>
        FilterReferences(_references.Incoming(targetEntityId), fieldKey);

    public IReadOnlyList<ProjectEntity> Nearby(
        ProjectVector3 center,
        float radius,
        ProjectEntityKind? kind = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(center);
        if (!float.IsFinite(radius) || radius < 0 || radius > MaximumQueryRadius)
            throw new ArgumentOutOfRangeException(nameof(radius));
        var span = (long)Math.Ceiling(radius / CellSize);
        var origin = Cell(center);
        var radiusSquared = (double)radius * radius;
        var matches = new List<ProjectEntity>();
        for (var xOffset = -span; xOffset <= span; xOffset++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var yOffset = -span; yOffset <= span; yOffset++)
            for (var zOffset = -span; zOffset <= span; zOffset++)
            {
                if (!TryAdd(origin.X, xOffset, out var x)
                    || !TryAdd(origin.Y, yOffset, out var y)
                    || !TryAdd(origin.Z, zOffset, out var z)) continue;
                if (!_spatialCells.TryGetValue((x, y, z), out var candidates)) continue;
                foreach (var candidate in candidates)
                {
                    if (kind is { } expected && ProjectReferences.KindOf(candidate) != expected) continue;
                    var position = candidate.Transform.Position;
                    var dx = (double)position.X - center.X;
                    var dy = (double)position.Y - center.Y;
                    var dz = (double)position.Z - center.Z;
                    if (dx * dx + dy * dy + dz * dz <= radiusSquared) matches.Add(candidate);
                }
            }
        }
        return Sort(matches);
    }

    private static ProjectEntity[] Sort(IEnumerable<ProjectEntity> entities) => entities
        .OrderBy(entity => entity.EntityId.ToString(), StringComparer.Ordinal)
        .ToArray();

    private static IReadOnlyList<ProjectReferenceEdge> FilterReferences(
        IReadOnlyList<ProjectReferenceEdge> references,
        string? fieldKey) => fieldKey is null
        ? references
        : references.Where(reference => reference.Reference.FieldKey == fieldKey).ToArray();

    private static (long X, long Y, long Z) Cell(ProjectVector3 position) =>
        (CellCoordinate(position.X), CellCoordinate(position.Y), CellCoordinate(position.Z));

    private static long CellCoordinate(float value)
    {
        var coordinate = Math.Floor((double)value / CellSize);
        return coordinate <= long.MinValue ? long.MinValue : coordinate >= long.MaxValue ? long.MaxValue : (long)coordinate;
    }

    private static bool TryAdd(long value, long offset, out long result)
    {
        if (offset > 0 && value > long.MaxValue - offset
            || offset < 0 && value < long.MinValue - offset)
        {
            result = default;
            return false;
        }
        result = value + offset;
        return true;
    }
}
