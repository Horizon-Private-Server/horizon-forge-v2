namespace Forge.Host.Domain;

public sealed partial class ForgeProjectWorkspace
{
    internal IReadOnlyList<EditorTransformUpdate> ExpandCollisionLinkTransforms(
        IReadOnlyList<EditorTransformUpdate> updates)
    {
        ArgumentNullException.ThrowIfNull(updates);
        var entities = Content.Entities.ToDictionary(entity => entity.EntityId);
        var explicitUpdates = updates.ToDictionary(update => update.EntityId);
        var adjacency = CollisionLinkAdjacency(entities);
        var result = explicitUpdates.ToDictionary(pair => pair.Key, pair => pair.Value);
        var visited = new HashSet<EntityId>();
        foreach (var start in explicitUpdates.Keys)
        {
            if (!adjacency.ContainsKey(start) || !visited.Add(start)) continue;
            var component = new List<EntityId>();
            var pending = new Queue<EntityId>();
            pending.Enqueue(start);
            while (pending.TryDequeue(out var current))
            {
                component.Add(current);
                foreach (var linked in adjacency[current])
                    if (visited.Add(linked)) pending.Enqueue(linked);
            }
            var changed = component.Where(explicitUpdates.ContainsKey)
                .Where(entityId => entities[entityId].Transform != explicitUpdates[entityId].Transform)
                .Select(entityId => (EntityId: entityId, Delta: TransformDelta(
                    entities[entityId].Transform, explicitUpdates[entityId].Transform)))
                .ToArray();
            if (changed.Length == 0) continue;
            var collisionChanges = changed.Where(value => entities[value.EntityId].Collision is not null).ToArray();
            var delta = (collisionChanges.Length > 0 ? collisionChanges : changed)[0].Delta;
            if (!Rigid(delta))
                throw new ArgumentException("Entities linked to source collision do not support scaling.", nameof(updates));
            foreach (var entityId in component)
            {
                var entity = entities[entityId];
                var transform = entity.Collision is null
                    && explicitUpdates.TryGetValue(entityId, out var explicitUpdate)
                    ? explicitUpdate.Transform
                    : Apply(entity.Transform, delta);
                result[entityId] = new(entityId, transform);
            }
        }
        return result.Values.OrderBy(update => update.EntityId.ToString(), StringComparer.Ordinal).ToArray();
    }

    internal int ReplaceCollisionLinks(IReadOnlyDictionary<EntityId, IReadOnlyList<EntityId>> links)
    {
        ArgumentNullException.ThrowIfNull(links);
        var changed = 0;
        var entities = Content.Entities.Select(entity =>
        {
            if (entity.Collision is not { } collision
                || !links.TryGetValue(entity.EntityId, out var next)) return entity;
            if (collision.LinkedInstanceEntityIds is not null
                && collision.LinkedInstanceEntityIds.SequenceEqual(next)) return entity;
            changed++;
            return entity with { Collision = collision with { LinkedInstanceEntityIds = next.ToArray() } };
        }).ToArray();
        if (changed == 0) return 0;
        var previous = Content;
        Content = Content with { Entities = entities };
        try
        {
            Validate();
            return changed;
        }
        catch
        {
            Content = previous;
            throw;
        }
    }

    private static Dictionary<EntityId, HashSet<EntityId>> CollisionLinkAdjacency(
        IReadOnlyDictionary<EntityId, ProjectEntity> entities)
    {
        var adjacency = new Dictionary<EntityId, HashSet<EntityId>>();
        foreach (var collisionEntity in entities.Values.Where(entity =>
                     entity.Collision?.LinkedInstanceEntityIds is { Count: > 0 }))
        foreach (var instanceId in collisionEntity.Collision!.LinkedInstanceEntityIds!)
        {
            if (!entities.ContainsKey(instanceId)) continue;
            AddEdge(collisionEntity.EntityId, instanceId);
            AddEdge(instanceId, collisionEntity.EntityId);
        }
        return adjacency;

        void AddEdge(EntityId source, EntityId target)
        {
            if (!adjacency.TryGetValue(source, out var values)) adjacency[source] = values = [];
            values.Add(target);
        }
    }

    private static System.Numerics.Matrix4x4 TransformDelta(ProjectTransform from, ProjectTransform to)
    {
        if (!System.Numerics.Matrix4x4.Invert(Matrix(from), out var inverse))
            throw new ArgumentException("Linked collision source transform is not invertible.");
        return inverse * Matrix(to);
    }

    private static ProjectTransform Apply(ProjectTransform value, System.Numerics.Matrix4x4 delta)
    {
        if (!System.Numerics.Matrix4x4.Decompose(
                Matrix(value) * delta, out _, out var rotation, out var position))
            throw new ArgumentException("Linked collision transform cannot be decomposed.");
        rotation = System.Numerics.Quaternion.Normalize(rotation);
        return new(
            new(position.X, position.Y, position.Z),
            new(rotation.X, rotation.Y, rotation.Z, rotation.W),
            value.Scale);
    }

    private static System.Numerics.Matrix4x4 Matrix(ProjectTransform value)
    {
        var rotation = System.Numerics.Quaternion.Normalize(new(
            value.Rotation.X, value.Rotation.Y, value.Rotation.Z, value.Rotation.W));
        return System.Numerics.Matrix4x4.CreateScale(value.Scale.X, value.Scale.Y, value.Scale.Z)
            * System.Numerics.Matrix4x4.CreateFromQuaternion(rotation)
            * System.Numerics.Matrix4x4.CreateTranslation(value.Position.X, value.Position.Y, value.Position.Z);
    }

    private static bool Rigid(System.Numerics.Matrix4x4 value) =>
        System.Numerics.Matrix4x4.Decompose(value, out var scale, out _, out _)
        && MathF.Abs(scale.X - 1) <= 0.0001f
        && MathF.Abs(scale.Y - 1) <= 0.0001f
        && MathF.Abs(scale.Z - 1) <= 0.0001f;

}
