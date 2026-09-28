using Forge.Host.Domain;

namespace Forge.Host.Games.UYA;

internal static class UyaEditorCapabilities
{
    public static EditorTransformCapabilities ResolveTransformCapabilities(ProjectEntity entity)
    {
        const EditorTransformCapabilities all = EditorTransformCapabilities.Translate
            | EditorTransformCapabilities.Rotate | EditorTransformCapabilities.Scale;
        if (entity.Provenance is not { Game: "UYA" }) return EditorTransformCapabilities.None;
        if (entity.Camera is not null) return EditorTransformCapabilities.Translate | EditorTransformCapabilities.Rotate;
        if (entity.AmbientSound is not null || entity.Geometry?.Spline is not null
            || entity.Geometry?.GrindPath is not null) return all;
        if (entity.Lighting?.DirectionalLight is not null) return EditorTransformCapabilities.Rotate;
        if (entity.Lighting?.PointLight is not null)
            return EditorTransformCapabilities.Translate | EditorTransformCapabilities.Scale;
        if (entity.Lighting?.EnvironmentSamplePoint is not null) return EditorTransformCapabilities.Translate;
        if (entity.Lighting?.EnvironmentTransition is not null) return all;
        var shape = entity.Geometry is { } geometry
            ? geometry.Cuboid ?? geometry.Sphere ?? geometry.Cylinder ?? geometry.Pill
            : null;
        return shape is not null ? all : EditorTransformCapabilities.None;
    }
}
