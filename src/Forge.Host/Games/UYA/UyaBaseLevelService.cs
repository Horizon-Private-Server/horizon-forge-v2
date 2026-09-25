using Forge.Host.Domain;
using System.Numerics;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.Gameplay;
using RatchetPs2.Games.DL.Level;
using RatchetPs2.Games.UYA.Gameplay;
using RatchetPs2.Games.UYA.Level;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.UYA;

internal static class UyaBaseLevelService
{
    public static UyaBaseLevelData Read(Stream iso, AssetCatalogStore catalog, int level)
    {
        var levelWad = LevelArchiveReader.ExtractPrimary(GameId.UYA, iso, level);
        var package = UyaLevelWadUnpacker.Unpack(levelWad);
        var files = package.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        var mobyBytes = Required(files, "gameplay/core/moby_instances.bin", level);
        var tieBytes = Required(files, "gameplay/core/tie_instances.bin", level);
        var shrubBytes = Required(files, "gameplay/core/shrub_instances.bin", level);
        var gameplay = UyaGameplayBlockReader.ReadCore(
            Required(files, "gameplay/gameplay_core.bin", level));
        var levelSettings = gameplay.Blocks.SingleOrDefault(value => value.SemanticName == "level_settings")?.LevelSettings;
        var mobyTable = mobyBytes.Length == 0
            ? new UyaMobyInstances(0, 0, 0, 0, [], [])
            : UyaMobyInstancesReader.Read(mobyBytes);
        var mobys = mobyTable.Instances;
        var tieTable = tieBytes.Length == 0
            ? new UyaTieInstances(0, [0, 0, 0], [], [])
            : UyaTieInstancesReader.Read(tieBytes);
        var shrubTable = shrubBytes.Length == 0
            ? new UyaShrubInstances(0, [0, 0, 0], [], [])
            : UyaShrubInstancesReader.Read(shrubBytes);
        var ties = tieTable.Instances;
        var shrubs = shrubTable.Instances;

        var source = UyaLevelWadRenderPackageBuilder.ReadAssetSourceFiles(package.Files);
        var header = DlAssetReader.ReadHeader(source.HeaderBytes);
        var mobyClasses = ModelClasses(source.HeaderBytes, header.MobyModelOffset, header.MobyModelCount);
        var tieClasses = ModelClasses(source.HeaderBytes, header.TieModelOffset, header.TieModelCount);
        var shrubClasses = DlAssetReader.ReadShrubDefinitions(source.HeaderBytes, header.ShrubModelOffset, header.ShrubModelCount)
            .Where(model => model.ModelOffset > 0).Select(model => model.ModelId).ToHashSet();
        var mobyAssets = AssetLookup(catalog, AssetKind.Moby, "moby", level);
        var tieAssets = AssetLookup(catalog, AssetKind.Tie, "tie", level);
        var shrubAssets = AssetLookup(catalog, AssetKind.Shrub, "shrub", level);

        var geometry = gameplay.Geometry;
        var lighting = gameplay.Lighting;
        var cameras = gameplay.Blocks.FirstOrDefault(value => value.CameraInstances is not null)?.CameraInstances?.Instances ?? [];
        var sounds = gameplay.Blocks.FirstOrDefault(value => value.SoundInstances is not null)?.SoundInstances?.Instances ?? [];
        var cameraCollision = gameplay.Blocks.FirstOrDefault(value => value.CameraCollisionGrid is not null)
            ?.CameraCollisionGrid?.Primitives ?? [];
        var entities = new List<ProjectEntity>(
            mobys.Count + ties.Count + shrubs.Count + geometry.Cuboids.Length + geometry.Spheres.Length
            + geometry.Cylinders.Length + geometry.Pills.Length + geometry.Splines.Length
            + geometry.GrindPaths.Length + geometry.Areas.Length + lighting.DirectionalLights.Length
            + lighting.PointLights.Lights.Length + lighting.EnvironmentSamplePoints.Length
            + lighting.EnvironmentTransitions.Length + cameras.Count + sounds.Count);
        for (var index = 0; index < mobys.Count; index++)
        {
            var instance = mobys[index];
            entities.Add(new(
                EntityId.New(),
                $"Moby 0x{instance.ClassId:X4} #{index}",
                "mobys",
                new(
                    new(instance.Position.X, instance.Position.Y, instance.Position.Z),
                    FromZyxEuler(instance.Rotation),
                    new(instance.Scale, instance.Scale, instance.Scale)),
                Reference(mobyAssets, instance.ClassId, AssetKind.Moby),
                new("UYA", level, "gameplay/core/moby_instances", index),
                Source: new(instance.ClassId, mobyBytes.AsSpan(
                    UyaMobyInstancesReader.HeaderSize + index * UyaMobyInstancesReader.RecordSize,
                    UyaMobyInstancesReader.RecordSize).ToArray(), !mobyClasses.Contains(instance.ClassId))));
        }

        var fallbackTransforms = 0;
        for (var index = 0; index < ties.Count; index++)
        {
            var tie = ties[index];
            entities.Add(CreateStaticEntity(tie.ClassId, tie.Transform, tie.RawBytes,
                index, level, "Tie", "ties", "gameplay/core/tie_instances", AssetKind.Tie, tieAssets, ref fallbackTransforms) with
            {
                TieLighting = new(tie.DirectionalLights, lighting.TieAmbientRgbas[index]),
            });
        }
        for (var index = 0; index < shrubs.Count; index++)
            entities.Add(CreateStaticEntity(shrubs[index].ClassId, shrubs[index].Transform, shrubs[index].RawBytes,
                index, level, "Shrub", "shrubs", "gameplay/core/shrub_instances", AssetKind.Shrub, shrubAssets, ref fallbackTransforms));
        AddGeometryEntities(entities, geometry, cameraCollision, level, ref fallbackTransforms);
        AddLightingEntities(entities, lighting, level, ref fallbackTransforms);
        AddEnvironmentInstances(entities, cameras, sounds, level, ref fallbackTransforms);

        var missing = CountMissing(mobys.Select(value => value.ClassId), mobyClasses, mobyAssets)
            + CountMissing(ties.Select(value => value.ClassId), tieClasses, tieAssets)
            + CountMissing(shrubs.Select(value => value.ClassId), shrubClasses, shrubAssets);
        var missingClasses = MissingClasses(mobys.Select(value => value.ClassId), mobyClasses, mobyAssets)
            + MissingClasses(ties.Select(value => value.ClassId), tieClasses, tieAssets)
            + MissingClasses(shrubs.Select(value => value.ClassId), shrubClasses, shrubAssets);
        return new(
            entities,
            entities.Count,
            entities.Count(entity => entity.Asset is not null),
            mobys.Count(instance => !mobyClasses.Contains(instance.ClassId)),
            missing,
            missingClasses,
            fallbackTransforms,
            UyaOpaqueContentService.Capture(levelWad, package),
            UyaBaseLayerService.Extract(package),
            [
                new(BakeLayerId.Ties, UyaTieInstancesReader.RecordSize,
                    tieTable.HeaderWords, tieTable.TrailingBytes),
                new(BakeLayerId.Shrubs, UyaShrubInstancesReader.RecordSize,
                    shrubTable.HeaderWords, shrubTable.TrailingBytes),
                new(BakeLayerId.Mobys, UyaMobyInstancesReader.RecordSize,
                    [mobyTable.SpawnableMobyCount, mobyTable.Pad8, mobyTable.PadC], mobyTable.TrailingBytes),
            ],
            gameplay,
            levelSettings is null ? null : ProjectSettings(levelSettings));
    }

    private static ProjectLevelSettings ProjectSettings(UyaLevelSettings value) => new(
        new((byte)Math.Clamp(value.BackgroundColor.Red, 0, 255),
            (byte)Math.Clamp(value.BackgroundColor.Green, 0, 255),
            (byte)Math.Clamp(value.BackgroundColor.Blue, 0, 255)),
        new((byte)Math.Clamp(value.FogColor.Red, 0, 255),
            (byte)Math.Clamp(value.FogColor.Green, 0, 255),
            (byte)Math.Clamp(value.FogColor.Blue, 0, 255)),
        Math.Max(0, Finite(value.FogNearDistance) / 1024),
        Math.Max(0, Finite(value.FogFarDistance) / 1024),
        Math.Clamp(Finite(value.FogNearIntensity), 0, 255),
        Math.Clamp(Finite(value.FogFarIntensity), 0, 255));

    private static float Finite(float value) => float.IsFinite(value) ? value : 0;

    private static void AddEnvironmentInstances(
        ICollection<ProjectEntity> entities,
        IReadOnlyList<UyaCameraInstance> cameras,
        IReadOnlyList<UyaSoundInstance> sounds,
        int level,
        ref int fallbackTransforms)
    {
        for (var index = 0; index < cameras.Count; index++)
        {
            var value = cameras[index];
            entities.Add(new(
                EntityId.New(),
                $"Camera 0x{value.Type:X4} #{index}",
                "cameras",
                new(
                    new(value.Position.X, value.Position.Y, value.Position.Z),
                    FromZyxEuler(new(value.Rotation.X, value.Rotation.Y, value.Rotation.Z)),
                    new(1, 1, 1)),
                null,
                new("UYA", level, "gameplay/core/cameras", index),
                Source: new(value.Type, value.RawBytes, false),
                Camera: new(value.PvarIndex, new(value.Rotation.X, value.Rotation.Y, value.Rotation.Z))));
        }
        for (var index = 0; index < sounds.Count; index++)
        {
            var value = sounds[index];
            var transform = Decompose(value.Matrix);
            if (transform is null)
            {
                fallbackTransforms++;
                transform = ProjectTransform.Identity;
            }
            entities.Add(new(
                EntityId.New(),
                $"Ambient Sound 0x{value.ClassId:X4} #{index}",
                "ambient sounds",
                transform,
                null,
                new("UYA", level, "gameplay/core/sound_instances", index),
                Source: new(value.ClassId, value.RawBytes, false),
                AmbientSound: new(
                    value.MissionClass, value.UpdateFunctionPointer, value.PvarIndex, value.Range,
                    value.Matrix, value.InverseRotationMatrix,
                    new(value.Rotation.X, value.Rotation.Y, value.Rotation.Z), value.Padding)));
        }
    }

    private static void AddLightingEntities(
        ICollection<ProjectEntity> entities,
        UyaGameplayLighting lighting,
        int level,
        ref int fallbackTransforms)
    {
        foreach (var value in lighting.DirectionalLights)
        {
            entities.Add(new(
                EntityId.New(),
                $"Directional Light #{value.Index}",
                "directional lights",
                ProjectTransform.Identity,
                null,
                new("UYA", level, "gameplay/core/directional_lights", value.Index),
                Lighting: new(DirectionalLight: new(
                    Vector(value.TopColor), Vector(value.TopDirection),
                    Vector(value.InverseColor), Vector(value.InverseDirection)))));
        }
        foreach (var value in lighting.PointLights.Lights)
        {
            var radius = value.Radius > 0 ? value.Radius : 1;
            entities.Add(new(
                EntityId.New(),
                $"Point Light #{value.Index}",
                "point lights",
                new(
                    new(value.Position.X, value.Position.Y, value.Position.Z),
                    ProjectTransform.Identity.Rotation,
                    new(radius, radius, radius)),
                null,
                new("UYA", level, "gameplay/core/point_lights", value.Index),
                Lighting: new(PointLight: new(
                    value.PositionX, value.PositionY, value.PositionZ, value.PackedRadius,
                    value.ColorR, value.ColorG, value.ColorB, value.UnknownE))));
        }
        foreach (var value in lighting.EnvironmentSamplePoints)
        {
            entities.Add(new(
                EntityId.New(),
                $"Environment Sample #{value.Index}",
                "environment samples",
                new(
                    new(value.Position.X, value.Position.Y, value.Position.Z),
                    ProjectTransform.Identity.Rotation,
                    new(2, 2, 2)),
                null,
                new("UYA", level, "gameplay/core/env_sample_points", value.Index),
                Lighting: new(EnvironmentSamplePoint: new(
                    value.HeroLight, value.PositionX, value.PositionY, value.PositionZ,
                    value.ReverbDepth, value.MusicTrack, value.FogNearIntensity, value.FogFarIntensity,
                    new(value.HeroColor.R, value.HeroColor.G, value.HeroColor.B),
                    value.ReverbType, value.ReverbDelay, value.ReverbFeedback, value.EnableReverbParameters,
                    new(value.FogColor.R, value.FogColor.G, value.FogColor.B),
                    value.FogNearDistance, value.FogFarDistance, value.Unknown1E))));
        }
        foreach (var value in lighting.EnvironmentTransitions)
        {
            var transform = InvertAndDecompose(value.InverseMatrix);
            if (transform is null)
            {
                fallbackTransforms++;
                var radius = float.IsFinite(value.BoundingSphere.W) && MathF.Abs(value.BoundingSphere.W) > 0
                    ? MathF.Abs(value.BoundingSphere.W) : 1;
                transform = new(
                    new(value.BoundingSphere.X, value.BoundingSphere.Y, value.BoundingSphere.Z),
                    ProjectTransform.Identity.Rotation,
                    new(radius, radius, radius));
            }
            entities.Add(new(
                EntityId.New(),
                $"Environment Transition #{value.Index}",
                "environment transitions",
                transform,
                null,
                new("UYA", level, "gameplay/core/env_transitions", value.Index),
                Lighting: new(EnvironmentTransition: new(
                    Vector(value.BoundingSphere), value.InverseMatrix,
                    Color(value.HeroColor1), Color(value.HeroColor2), value.HeroLight1, value.HeroLight2,
                    value.Flags, Color(value.FogColor1), Color(value.FogColor2),
                    value.FogNearDistance1, value.FogNearIntensity1, value.FogFarDistance1, value.FogFarIntensity1,
                    value.FogNearDistance2, value.FogNearIntensity2, value.FogFarDistance2, value.FogFarIntensity2,
                    value.Unknown7C))));
        }
    }

    private static ProjectVector4 Vector(GameplayVector4 value) => new(value.X, value.Y, value.Z, value.W);

    private static ProjectRgba32 Color(UyaRgba32 value) => new(value.R, value.G, value.B, value.A);

    private static ProjectTransform? InvertAndDecompose(IReadOnlyList<float> inverse)
    {
        if (inverse.Count != 16) return null;
        var source = new Matrix4x4(
            inverse[0], inverse[1], inverse[2], inverse[3],
            inverse[4], inverse[5], inverse[6], inverse[7],
            inverse[8], inverse[9], inverse[10], inverse[11],
            inverse[12], inverse[13], inverse[14], inverse[15]);
        if (!Matrix4x4.Invert(source, out var matrix)) return null;
        return Decompose([
            matrix.M11, matrix.M12, matrix.M13, matrix.M14,
            matrix.M21, matrix.M22, matrix.M23, matrix.M24,
            matrix.M31, matrix.M32, matrix.M33, matrix.M34,
            matrix.M41, matrix.M42, matrix.M43, matrix.M44,
        ]);
    }

    private static void AddGeometryEntities(
        ICollection<ProjectEntity> entities,
        GameplayGeometry geometry,
        IReadOnlyList<UyaCameraCollisionPrimitive> cameraCollision,
        int level,
        ref int fallbackTransforms)
    {
        var collision = cameraCollision.ToDictionary(value => (value.Type, value.Index));
        var cuboids = new List<ProjectEntity>(geometry.Cuboids.Length);
        foreach (var value in geometry.Cuboids)
        {
            var transform = Decompose(value.Matrix);
            if (transform is null)
            {
                fallbackTransforms++;
                transform = ProjectTransform.Identity;
            }
            collision.TryGetValue((3, value.Index), out var cameraCollisionPrimitive);
            cuboids.Add(new(
                EntityId.New(),
                ShapeName("Cuboid", value.Index, cameraCollisionPrimitive),
                "cuboids",
                transform,
                null,
                new("UYA", level, "gameplay/core/cuboids", value.Index),
                null,
                Geometry: new(Cuboid: new(
                    value.Matrix,
                    value.InverseRotationMatrix,
                    new(value.Rotation.X, value.Rotation.Y, value.Rotation.Z),
                    CameraCollision(cameraCollisionPrimitive)))));
        }
        foreach (var cuboid in cuboids) entities.Add(cuboid);

        var spheres = AddShapes(entities, geometry.Spheres, collision, 5, level, "Sphere", "spheres", "spheres",
            shape => new(Sphere: shape), ref fallbackTransforms);
        var cylinders = AddShapes(entities, geometry.Cylinders, collision, 6, level, "Cylinder", "cylinders", "cylinders",
            shape => new(Cylinder: shape), ref fallbackTransforms);
        AddShapes(entities, geometry.Pills, collision, 7, level, "Pill", "pills", "pills",
            shape => new(Pill: shape), ref fallbackTransforms);

        var splines = geometry.Splines.Select(value => new ProjectEntity(
            EntityId.New(),
            $"Spline #{value.Index}",
            "splines",
            ProjectTransform.Identity,
            null,
            new("UYA", level, "gameplay/core/splines", value.Index),
            null,
            Geometry: new(Spline: new(value.Points.Select(point =>
                new ProjectVector4(point.X, point.Y, point.Z, point.W)).ToArray())))).ToArray();
        foreach (var spline in splines) entities.Add(spline);

        foreach (var value in geometry.GrindPaths)
        {
            var bounds = new ProjectVector4(
                value.BoundingSphere.X, value.BoundingSphere.Y, value.BoundingSphere.Z, value.BoundingSphere.W);
            entities.Add(new(
                EntityId.New(),
                $"Grind Path #{value.Index}",
                "grind paths",
                ProjectTransform.Identity,
                null,
                new("UYA", level, "gameplay/core/grind_splines", value.Index),
                null,
                Geometry: new(GrindPath: new(
                    bounds,
                    value.Unknown4,
                    value.Wrap,
                    value.Inactive,
                    value.Points.Select(point => new ProjectVector4(point.X, point.Y, point.Z, point.W)).ToArray()))));
        }

        var cuboidIds = cuboids.ToDictionary(value => value.Provenance!.SourceIndex, value => value.EntityId);
        var sphereIds = spheres.ToDictionary(value => value.Provenance!.SourceIndex, value => value.EntityId);
        var cylinderIds = cylinders.ToDictionary(value => value.Provenance!.SourceIndex, value => value.EntityId);
        var splineIds = splines.ToDictionary(value => value.Provenance!.SourceIndex, value => value.EntityId);
        foreach (var value in geometry.Areas)
        {
            var bounds = new ProjectVector4(
                value.BoundingSphere.X, value.BoundingSphere.Y, value.BoundingSphere.Z, value.BoundingSphere.W);
            var radius = float.IsFinite(bounds.W) && MathF.Abs(bounds.W) > 0 ? MathF.Abs(bounds.W) : 1;
            entities.Add(new(
                EntityId.New(),
                $"Area #{value.Index}",
                "areas",
                new(
                    new(bounds.X, bounds.Y, bounds.Z),
                    new(0, 0, 0, 1),
                    new(radius, radius, radius)),
                null,
                new("UYA", level, "gameplay/core/areas", value.Index),
                null,
                Geometry: new(Area: new(
                    bounds,
                    value.LastUpdateTime,
                    Links(value.SplineIndices, splineIds),
                    Links(value.CuboidIndices, cuboidIds),
                    Links(value.SphereIndices, sphereIds),
                    Links(value.CylinderIndices, cylinderIds),
                    Links(value.NegativeCuboidIndices, cuboidIds)))));
        }
    }

    private static List<ProjectEntity> AddShapes(
        ICollection<ProjectEntity> entities,
        IReadOnlyList<GameplayShape> shapes,
        IReadOnlyDictionary<(int Type, int Index), UyaCameraCollisionPrimitive> cameraCollision,
        int cameraCollisionType,
        int level,
        string name,
        string layer,
        string section,
        Func<ProjectShapeGeometry, ProjectEntityGeometry> geometry,
        ref int fallbackTransforms)
    {
        var added = new List<ProjectEntity>(shapes.Count);
        foreach (var value in shapes)
        {
            cameraCollision.TryGetValue((cameraCollisionType, value.Index), out var cameraCollisionPrimitive);
            var transform = Decompose(value.Matrix);
            if (transform is null)
            {
                fallbackTransforms++;
                transform = ProjectTransform.Identity;
            }
            added.Add(new(
                EntityId.New(),
                ShapeName(name, value.Index, cameraCollisionPrimitive),
                layer,
                transform,
                null,
                new("UYA", level, $"gameplay/core/{section}", value.Index),
                null,
                Geometry: geometry(new(
                    value.Matrix,
                    value.InverseRotationMatrix,
                    new(value.Rotation.X, value.Rotation.Y, value.Rotation.Z),
                    CameraCollision(cameraCollisionPrimitive)))));
        }
        foreach (var entity in added) entities.Add(entity);
        return added;
    }

    private static string ShapeName(string name, int index, UyaCameraCollisionPrimitive? collision) =>
        collision is null ? $"{name} #{index}" : $"{name} #{index} · Camera Collision";

    private static ProjectCameraCollision? CameraCollision(UyaCameraCollisionPrimitive? value) => value is null
        ? null
        : new(value.Flags, value.IntValue, value.FloatValue,
            new(value.BoundingSphere.X, value.BoundingSphere.Y, value.BoundingSphere.Z, value.BoundingSphere.W));

    private static ProjectGeometryLink[] Links(
        IEnumerable<int> sourceIndices,
        IReadOnlyDictionary<int, EntityId>? entities = null) => sourceIndices
        .Select(index => new ProjectGeometryLink(
            index,
            entities is not null && entities.TryGetValue(index, out var entityId) ? entityId : null))
        .ToArray();

    private static ProjectEntity CreateStaticEntity(
        int classId,
        UyaInstanceTransform sourceTransform,
        byte[] rawRecord,
        int index,
        int level,
        string familyName,
        string layer,
        string section,
        AssetKind kind,
        IReadOnlyDictionary<int, AssetCatalogEntry> assets,
        ref int fallbackTransforms)
    {
        var transform = Decompose(sourceTransform);
        if (transform is null)
        {
            fallbackTransforms++;
            transform = new(
                new(sourceTransform.Position.X, sourceTransform.Position.Y, sourceTransform.Position.Z),
                new(0, 0, 0, 1),
                new(1, 1, 1));
        }
        return new(
            EntityId.New(),
            $"{familyName} 0x{classId:X4} #{index}",
            layer,
            transform,
            Reference(assets, classId, kind),
            new("UYA", level, section, index),
            Source: new(classId, rawRecord));
    }

    internal static ProjectTransform? Decompose(UyaInstanceTransform value)
    {
        var basisX = new Vector3(value.BasisX.X, value.BasisX.Y, value.BasisX.Z);
        var basisY = new Vector3(value.BasisY.X, value.BasisY.Y, value.BasisY.Z);
        var basisZ = new Vector3(value.BasisZ.X, value.BasisZ.Y, value.BasisZ.Z);
        var position = new Vector3(value.Position.X, value.Position.Y, value.Position.Z);
        return Decompose(basisX, basisY, basisZ, position);
    }

    internal static ProjectTransform? Decompose(IReadOnlyList<float> matrix)
    {
        if (matrix.Count != 16) return null;
        return Decompose(
            new(matrix[0], matrix[1], matrix[2]),
            new(matrix[4], matrix[5], matrix[6]),
            new(matrix[8], matrix[9], matrix[10]),
            new(matrix[12], matrix[13], matrix[14]));
    }

    private static ProjectTransform? Decompose(Vector3 basisX, Vector3 basisY, Vector3 basisZ, Vector3 position)
    {
        var scale = new Vector3(basisX.Length(), basisY.Length(), basisZ.Length());
        var matrix = new Matrix4x4(
            basisX.X, basisX.Y, basisX.Z, 0,
            basisY.X, basisY.Y, basisY.Z, 0,
            basisZ.X, basisZ.Y, basisZ.Z, 0,
            position.X, position.Y, position.Z, 1);
        if (matrix.GetDeterminant() < 0) scale.X = -scale.X;
        if (scale.X == 0 || scale.Y == 0 || scale.Z == 0 || !Finite(scale) || !Finite(position)) return null;
        var rotation = Quaternion.CreateFromRotationMatrix(new(
            basisX.X / scale.X, basisX.Y / scale.X, basisX.Z / scale.X, 0,
            basisY.X / scale.Y, basisY.Y / scale.Y, basisY.Z / scale.Y, 0,
            basisZ.X / scale.Z, basisZ.Y / scale.Z, basisZ.Z / scale.Z, 0,
            0, 0, 0, 1));
        if (!float.IsFinite(rotation.X) || !float.IsFinite(rotation.Y)
            || !float.IsFinite(rotation.Z) || !float.IsFinite(rotation.W)
            || rotation.LengthSquared() == 0) return null;
        rotation = Quaternion.Normalize(rotation);
        return new(
            new(position.X, position.Y, position.Z),
            new(rotation.X, rotation.Y, rotation.Z, rotation.W),
            new(scale.X, scale.Y, scale.Z));
    }

    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    internal static ProjectQuaternion FromZyxEuler(UyaVector3 rotation)
    {
        var c1 = MathF.Cos(rotation.X / 2);
        var c2 = MathF.Cos(rotation.Y / 2);
        var c3 = MathF.Cos(rotation.Z / 2);
        var s1 = MathF.Sin(rotation.X / 2);
        var s2 = MathF.Sin(rotation.Y / 2);
        var s3 = MathF.Sin(rotation.Z / 2);
        return new(
            s1 * c2 * c3 - c1 * s2 * s3,
            c1 * s2 * c3 + s1 * c2 * s3,
            c1 * c2 * s3 - s1 * s2 * c3,
            c1 * c2 * c3 + s1 * s2 * s3);
    }

    private static HashSet<int> ModelClasses(byte[] header, int offset, int count) =>
        DlAssetReader.ReadModelDefinitions(header, offset, count)
            .Where(model => model.ModelOffset > 0).Select(model => model.ModelId).ToHashSet();

    private static IReadOnlyDictionary<int, AssetCatalogEntry> AssetLookup(
        AssetCatalogStore catalog,
        AssetKind kind,
        string aliasPrefix,
        int level)
    {
        var result = new Dictionary<int, AssetCatalogEntry>();
        foreach (var asset in catalog.Query(new(Kind: kind, Game: "UYA", Level: $"level{level:00}", Limit: AssetCatalogStore.MaxQueryLimit)))
        {
            if (asset.CanonicalFormatVersion != UyaAssetImportService.CanonicalFormatVersion) continue;
            var prefix = $"{aliasPrefix}:";
            var alias = asset.Aliases.FirstOrDefault(value => value.StartsWith(prefix, StringComparison.Ordinal)
                && !value.StartsWith($"{prefix}0x", StringComparison.Ordinal));
            if (alias is not null && int.TryParse(alias.AsSpan(prefix.Length), out var classId)) result.TryAdd(classId, asset);
        }
        return result;
    }

    private static ProjectAssetReference? Reference(
        IReadOnlyDictionary<int, AssetCatalogEntry> assets,
        int classId,
        AssetKind kind) => assets.TryGetValue(classId, out var asset) ? new(asset.Id, kind) : null;

    private static int CountMissing(
        IEnumerable<int> instances,
        IReadOnlySet<int> modelClasses,
        IReadOnlyDictionary<int, AssetCatalogEntry> assets) =>
        instances.Count(classId => modelClasses.Contains(classId) && !assets.ContainsKey(classId));

    private static int MissingClasses(
        IEnumerable<int> instances,
        IReadOnlySet<int> modelClasses,
        IReadOnlyDictionary<int, AssetCatalogEntry> assets) =>
        instances.Where(classId => modelClasses.Contains(classId) && !assets.ContainsKey(classId)).Distinct().Count();

    private static byte[] Required(
        IReadOnlyDictionary<string, RatchetPs2.Core.Wad.Models.PackedFile> files,
        string path,
        int level) => files.TryGetValue(path, out var file)
            ? file.Bytes
            : throw new InvalidDataException($"UYA level {level} does not contain readable {Path.GetFileNameWithoutExtension(path)} data.");
}

internal sealed record UyaBaseLevelData(
    IReadOnlyList<ProjectEntity> Entities,
    int SourceInstanceCount,
    int RenderableInstanceCount,
    int ModelLessInstanceCount,
    int MissingInstanceCount,
    int MissingClassCount,
    int FallbackTransformCount,
    IReadOnlyList<OpaqueSectionCapture> OpaqueSections,
    IReadOnlyList<UyaBaseLayerPayload> BaseLayers,
    IReadOnlyList<UyaStaticLayerSourceRecord> StaticLayers,
    UyaGameplayBlocks Gameplay,
    ProjectLevelSettings? LevelSettings);
