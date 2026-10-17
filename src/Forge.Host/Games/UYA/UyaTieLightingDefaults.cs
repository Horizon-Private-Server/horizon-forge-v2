using Forge.Host.Domain;
using RatchetPs2.Games.UYA.Gameplay;

namespace Forge.Host.Games.UYA;

internal static class UyaTieLightingDefaults
{
    public static ProjectTieLighting Create(
        IEnumerable<ProjectEntity> entities,
        ProjectVector3 position,
        int ambientSize)
    {
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(position);
        var ambient = UyaGameplayInstanceTemplates.CreateNeutralTieAmbient(ambientSize);
        var nearest = entities
            .Where(entity => entity.Provenance is not null
                && entity.TieLighting?.AmbientRgbas.Length >= 4)
            .MinBy(entity => DistanceSquared(position, entity.Transform.Position));
        if (nearest?.TieLighting is not { } source) return new(0, ambient);
        source.AmbientRgbas.AsSpan(0, Math.Min(4, ambient.Length)).CopyTo(ambient);
        return new(source.DirectionalLights, ambient);
    }

    private static double DistanceSquared(ProjectVector3 left, ProjectVector3 right)
    {
        var x = (double)left.X - right.X;
        var y = (double)left.Y - right.Y;
        var z = (double)left.Z - right.Z;
        return x * x + y * y + z * z;
    }
}
