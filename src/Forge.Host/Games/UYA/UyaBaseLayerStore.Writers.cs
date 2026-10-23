using Forge.Host.Domain;
using System.Text.Json;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.Skyboxes;
using RatchetPs2.Core.Tfrags;
using RatchetPs2.Games.UYA.Gameplay;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.UYA;

public static partial class UyaBaseLayerStore
{
    private static byte[] WriteLevelSettings(
        byte[] source,
        ProjectLevelSettings settings,
        ForgeProjectWorkspace workspace)
    {
        var original = UyaLevelSettingsReader.Read(source);
        var scene = settings.Scene;
        return UyaLevelSettingsWriter.Write(source, new(
            new(settings.BackgroundColor.R, settings.BackgroundColor.G, settings.BackgroundColor.B),
            new(settings.FogColor.R, settings.FogColor.G, settings.FogColor.B),
            settings.FogNearDistance * 1024,
            settings.FogFarDistance * 1024,
            settings.FogNearIntensity,
            settings.FogFarIntensity,
            scene?.DeathHeight ?? original.DeathHeight,
            scene?.IsSphericalWorld ?? original.IsSphericalWorld,
            scene is null ? original.SphereCenter : NativeVector(scene.SphereCenter),
            scene is null ? original.ShipPosition : NativeVector(scene.ShipPosition),
            scene?.ShipRotationZ ?? original.ShipRotationZ,
            scene is null ? original.ShipPath : ResolveLevelReference(
                workspace, scene.ShipPath, "gameplay/core/splines"),
            scene is null ? original.ShipCameraCuboidStart : ResolveLevelReference(
                workspace, scene.ShipCameraCuboidStart, "gameplay/core/cuboids"),
            scene is null ? original.ShipCameraCuboidEnd : ResolveLevelReference(
                workspace, scene.ShipCameraCuboidEnd, "gameplay/core/cuboids")));
    }

    private static UyaVector3 NativeVector(ProjectVector3 value) =>
        new(value.X, value.Y, value.Z);

    private static int ResolveLevelReference(
        ForgeProjectWorkspace workspace,
        ProjectIndexedReference value,
        string section)
    {
        if (value.Reference.EntityId is not { } target)
        {
            if (value.SourceValue == -1) return -1;
            throw new InvalidDataException(
                $"Level setting {value.Reference.FieldKey} cannot resolve source index {value.SourceValue}.");
        }
        var entity = workspace.Content.Entities.SingleOrDefault(candidate => candidate.EntityId == target);
        if (entity?.Provenance?.Section != section)
            throw new InvalidDataException($"Level setting {value.Reference.FieldKey} targets a missing entity.");
        return entity.Provenance.SourceIndex;
    }

    private static byte[] WriteTieAmbientRgbas(byte[] source, ForgeProjectWorkspace workspace)
    {
        var values = UyaStaticLayerStore.OrderedEntities(workspace, BakeLayerId.Ties)
            .Select(value => value.TieLighting?.AmbientRgbas
                ?? throw new InvalidDataException($"Tie {value.EntityId} has no ambient lighting data."))
            .ToArray();
        try
        {
            var original = UyaGameplayLightingReader.ReadTieAmbientRgbas(source, values.Length);
            if (original.Zip(values).All(pair => pair.First.SequenceEqual(pair.Second))) return source;
        }
        catch (InvalidDataException)
        {
            // A removed or reordered tie can make the source indices invalid; rebuild them below.
        }
        return UyaTieAmbientRgbasWriter.Write(values);
    }

    private static byte[] WritePointLights(byte[] source, ForgeProjectWorkspace workspace)
    {
        var table = UyaGameplayLightingReader.ReadPointLights(source);
        var entities = PointLights(workspace).ToArray();
        if (!entities.Select(value => value.Provenance!.SourceIndex)
                .SequenceEqual(Enumerable.Range(0, table.Lights.Length)))
            throw new InvalidDataException("Point lights have unsupported inserted, deleted, or reordered instances.");
        var edits = entities.Select((entity, index) => (entity, source: table.Lights[index]))
            .Where(value => !EquivalentPointLightTransform(value.entity.Transform, value.source))
            .Select(value =>
            {
                var transform = value.entity.Transform;
                if (!IdentityRotation(transform.Rotation)
                    || !Near(transform.Scale.X, transform.Scale.Y)
                    || !Near(transform.Scale.X, transform.Scale.Z))
                    throw new InvalidDataException($"Point light {value.entity.EntityId} requires identity rotation and uniform scale.");
                return new UyaPointLightEdit(value.entity.Provenance!.SourceIndex,
                    new(transform.Position.X, transform.Position.Y, transform.Position.Z), transform.Scale.X);
            }).ToArray();
        return UyaPointLightsWriter.Write(source, edits);
    }

    private static byte[] WriteDirectionalLights(byte[] source, ForgeProjectWorkspace workspace)
    {
        var entities = DirectionalLights(workspace).ToArray();
        if (!entities.Select(value => value.Provenance?.SourceIndex)
                .SequenceEqual(Enumerable.Range(0, entities.Length).Select(value => (int?)value)))
            throw new InvalidDataException("Directional lights must have contiguous source indexes.");
        var replaced = UyaDirectionalLightsWriter.Replace(source, entities.Select((value, index) =>
        {
            var light = value.Lighting!.DirectionalLight!;
            return new UyaDirectionalLight(index,
                Vector(light.TopColor), Vector(light.TopDirection),
                Vector(light.InverseColor), Vector(light.InverseDirection));
        }).ToArray());
        return UyaDirectionalLightsWriter.Write(replaced, entities
            .Where(value => !IdentityRotation(value.Transform.Rotation))
            .Select(value => new UyaDirectionalLightEdit(value.Provenance!.SourceIndex,
                new(value.Transform.Rotation.X, value.Transform.Rotation.Y,
                    value.Transform.Rotation.Z, value.Transform.Rotation.W))).ToArray());
    }

    private static RatchetPs2.Core.Gameplay.GameplayVector4 Vector(ProjectVector4 value) =>
        new(value.X, value.Y, value.Z, value.W);

    private static IEnumerable<ProjectEntity> DirectionalLights(ForgeProjectWorkspace workspace) =>
        workspace.Content.Entities.Where(value => value.Lighting?.DirectionalLight is not null)
            .OrderBy(value => value.Provenance?.SourceIndex ?? int.MaxValue);

    private static IEnumerable<ProjectEntity> PointLights(ForgeProjectWorkspace workspace) =>
        workspace.Content.Entities.Where(value => value.Lighting?.PointLight is not null)
            .OrderBy(value => value.Provenance?.SourceIndex ?? int.MaxValue);

    private static IEnumerable<ProjectEntity> EnabledSkyShells(ForgeProjectWorkspace workspace) =>
        workspace.Content.Entities.Where(value => value.SkyShell is not null && value.State?.Disabled != true)
            .OrderBy(value => value.SkyShell!.Order);

    private static bool EquivalentPointLightTransform(ProjectTransform transform, UyaPointLight source)
    {
        var radius = source.Radius > 0 ? source.Radius : 1;
        return Near(transform.Position.X, source.Position.X) && Near(transform.Position.Y, source.Position.Y)
            && Near(transform.Position.Z, source.Position.Z) && IdentityRotation(transform.Rotation)
            && Near(transform.Scale.X, radius) && Near(transform.Scale.Y, radius) && Near(transform.Scale.Z, radius);
    }

    private static bool IdentityRotation(ProjectQuaternion value) => Near(value.X, 0) && Near(value.Y, 0)
        && Near(value.Z, 0) && Near(MathF.Abs(value.W), 1);

    private static bool Near(float left, float right) => MathF.Abs(left - right) <= 0.00001f;

}
