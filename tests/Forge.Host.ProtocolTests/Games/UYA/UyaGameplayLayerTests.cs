using Forge.Host.Games.UYA;
using System.Buffers.Binary;
using System.Text.Json;
using Forge.Host.Domain;
using RatchetPs2.Core.Gameplay;
using RatchetPs2.Games.UYA.Gameplay;

namespace Forge.Host.ProtocolTests.Games.UYA;

internal static class UyaGameplayLayerTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-gameplay-bake-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var project = Path.Combine(root, "project");
            var moby = Moby("Moby", 0, new());
            var cuboid = Cuboid();
            var tables = Tables([]);
            var cuboidBytes = CuboidBytes();
            var cameraBytes = CameraBytes();
            var soundBytes = SoundBytes();
            var splineBytes = SplineBytes();
            var camera = Camera(cameraBytes);
            var sound = Sound(soundBytes);
            var spline = Spline();
            await CreateProjectAsync(project, [moby, cuboid, camera, sound, spline]);
            await UyaGameplayLayerStore.WriteSourceAsync(
                project, Source(), Gameplay(tables, cuboidBytes, cameraBytes, soundBytes, splineBytes));

            var input = await UyaGameplayLayerStore.CreateBakeInputAsync(project);
            Equal(0, input.Blockers!.Count, "valid gameplay input");
            var plan = BakeLayerGraph.CreatePlan(Context(),
            [
                new(BakeLayerId.Mobys, "mobys"u8.ToArray(), [], ReadOnlyMemory<byte>.Empty),
                input,
            ]);
            var staging = await BakeStagingStore.OpenAsync(project);
            var snapshot = await UyaGameplayLayerStore.StageAsync(
                project, staging, plan.Layers.Single(value => value.Layer == BakeLayerId.Gameplay));
            var output = Path.Combine(staging.RootPath, snapshot.RelativePath);
            var manifest = JsonSerializer.Deserialize<UyaGameplayBakeManifest>(
                await File.ReadAllBytesAsync(Path.Combine(output, "manifest.json")),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("Gameplay bake manifest is empty.");
            Equal(moby.EntityId, manifest.Mobys.Single().EntityId, "moby target mapping");
            Equal(0, manifest.Mobys.Single().PvarIndex, "moby pvar mapping");
            Equal(true, manifest.Instances.Select(value => value.EntityId)
                .SequenceEqual([camera.EntityId, sound.EntityId, cuboid.EntityId, spline.EntityId]),
                "gameplay instance target mappings");
            Equal(true, (await File.ReadAllBytesAsync(Path.Combine(output, "pvars", "pvar_data.bin")))
                .SequenceEqual(tables.DataBytes), "pvar data preserved");
            var rebuiltCuboidBytes = await File.ReadAllBytesAsync(Path.Combine(output, "pvars", "cuboids.bin"));
            var rebuiltCuboid = GameplayGeometryReader.ReadCuboids(rebuiltCuboidBytes).Single();
            Equal(100f, rebuiltCuboid.Matrix[12], "cuboid X serialized");
            Equal(200f, rebuiltCuboid.Matrix[13], "cuboid Y serialized");
            Equal(300f, rebuiltCuboid.Matrix[14], "cuboid Z serialized");
            Equal((byte)0x7f, rebuiltCuboidBytes[0x8c], "cuboid unknown bytes preserved");
            var rebuiltCamera = UyaCameraInstancesReader.Read(
                await File.ReadAllBytesAsync(Path.Combine(output, "pvars", "cameras.bin"))).Instances.Single();
            Equal(new GameplayVector3(10, 20, 30), rebuiltCamera.Position, "camera transform serialized");
            var rebuiltSound = UyaSoundInstancesReader.Read(
                await File.ReadAllBytesAsync(Path.Combine(output, "pvars", "sound_instances.bin"))).Instances.Single();
            Equal(40f, rebuiltSound.Matrix[12], "sound X serialized");
            Equal(50f, rebuiltSound.Matrix[13], "sound Y serialized");
            Equal(60f, rebuiltSound.Matrix[14], "sound Z serialized");
            var rebuiltSplineBytes = await File.ReadAllBytesAsync(Path.Combine(output, "pvars", "splines.bin"));
            var rebuiltSpline = GameplayGeometryReader.ReadSplines(rebuiltSplineBytes).Single();
            Equal(new GameplayVector4(12, 26, 42, 9), rebuiltSpline.Points[0], "spline first point serialized");
            Equal(new GameplayVector4(20, 38, 58, 10), rebuiltSpline.Points[1], "spline second point serialized");
            Equal(new GameplayVector4(28, 50, 74, 12), rebuiltSpline.Points[2], "spline added point serialized");
            Equal((byte)0x7f, rebuiltSplineBytes[0x2c], "spline unknown bytes preserved");
            var repeated = await UyaGameplayLayerStore.StageAsync(
                project, staging, plan.Layers.Single(value => value.Layer == BakeLayerId.Gameplay));
            Equal(snapshot.OutputFingerprint, repeated.OutputFingerprint, "gameplay output deterministic");

            var badProject = Path.Combine(root, "bad-project");
            var invalid = Moby("Invalid pvar", 2, new());
            var disabled = Moby("Disabled dependent", 0, new(Disabled: true));
            await CreateProjectAsync(badProject, [invalid, disabled]);
            await UyaGameplayLayerStore.WriteSourceAsync(badProject, Source(), Tables([1, 2]));
            var bad = await UyaGameplayLayerStore.CreateBakeInputAsync(badProject);
            var blockers = bad.Blockers ?? [];
            Equal(true, blockers.Any(value => value.Contains(invalid.EntityId.ToString(), StringComparison.Ordinal)
                && value.Contains("pvar index 2", StringComparison.Ordinal)), "invalid pvar identifies entity");
            Equal(true, blockers.Any(value => value.Contains("disabled moby", StringComparison.Ordinal)
                && value.Contains("index remapping", StringComparison.Ordinal)), "disabled dependent blocks gameplay");

            var collisionProject = Path.Combine(root, "camera-collision-project");
            var collisionShape = Cuboid();
            collisionShape = collisionShape with
            {
                Geometry = new(Cuboid: collisionShape.Geometry!.Cuboid! with
                {
                    CameraCollision = new(1, 2, 3, new(0, 0, 0, 1)),
                }),
            };
            await CreateProjectAsync(collisionProject, [collisionShape]);
            var collisionGrid = UyaCameraCollisionGridWriter.Write([
                new(3, 0, 1, 2, 3, new(0, 0, 0), new(0, 0, 0, 1), new(1, 1, 1)),
            ]);
            await UyaGameplayLayerStore.WriteSourceAsync(
                collisionProject, Source(), Gameplay(Tables([]), CuboidBytes(), [], [], cameraCollision: collisionGrid));
            await using (var runtime = new EditorRuntime(
                transformCapabilityResolver: UyaEditorCapabilities.ResolveTransformCapabilities))
            {
                var editorSnapshot = await runtime.OpenAsync(collisionProject, TimeSpan.Zero);
                Equal(EditorTransformCapabilities.Translate | EditorTransformCapabilities.Rotate
                    | EditorTransformCapabilities.Scale,
                    editorSnapshot.Entities.Single().TransformCapabilities,
                    "camera-collision shape exposes full transform support");
            }
            var collisionInput = await UyaGameplayLayerStore.CreateBakeInputAsync(collisionProject);
            Equal(0, collisionInput.Blockers!.Count, "moved camera-collision shape serializes");
            var collisionPlan = BakeLayerGraph.CreatePlan(Context(),
            [
                new(BakeLayerId.Mobys, "mobys"u8.ToArray(), [], ReadOnlyMemory<byte>.Empty),
                collisionInput,
            ]);
            var collisionStaging = await BakeStagingStore.OpenAsync(collisionProject);
            var collisionSnapshot = await UyaGameplayLayerStore.StageAsync(collisionProject, collisionStaging,
                collisionPlan.Layers.Single(value => value.Layer == BakeLayerId.Gameplay));
            var rebuiltCollision = UyaCameraCollisionGridReader.Read(await File.ReadAllBytesAsync(Path.Combine(
                collisionStaging.RootPath, collisionSnapshot.RelativePath, "pvars", "camera_collision_grid.bin")));
            var primitive = rebuiltCollision.Primitives.Single();
            Equal((3, 0, 1, 2, 3f),
                (primitive.Type, primitive.Index, primitive.Flags, primitive.IntValue, primitive.FloatValue),
                "camera-collision metadata preserved");
            Equal(new UyaVector4(100, 200, 0, MathF.Sqrt(29)), primitive.BoundingSphere,
                "camera-collision bounds regenerated");

            await VerifyGeometryImportAsync(root);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task VerifyGeometryImportAsync(string root)
    {
        var project = Path.Combine(root, "geometry-import-project");
        var retainedCuboid = WithCameraCollision(
            Shape("Retained cuboid", "cuboids", "cuboids", 0, 3, new(1, 2, 3)), 11);
        var removedCuboid = WithCameraCollision(
            Shape("Removed cuboid", "cuboids", "cuboids", 1, 3, new(4, 5, 6)), 12);
        var targetSphere = Shape("Target sphere", "spheres", "spheres", 0, 3, new(7, 8, 9));
        var targetSpline = Spline("Target spline", 0, 3, [new(1, 2, 3, 4)], 0x31);
        var targetPath = GrindPath("Target path", 0, 3, [new(5, 6, 7, 8)], 0x41);
        var targetArea = Area("Target area", 0, 3,
            cuboids: [Link(0, ProjectReferences.AreaCuboids, ProjectEntityKind.Cuboid, retainedCuboid.EntityId)]);
        await CreateProjectAsync(project,
            [retainedCuboid, removedCuboid, targetSphere, targetSpline, targetPath, targetArea]);
        var sourceCollision = UyaCameraCollisionGridWriter.Write([
            new(3, 0, 1, 11, 1, new(0, 0, 0), new(0, 0, 0, 1), new(1, 1, 1)),
            new(3, 1, 1, 12, 2, new(0, 0, 0), new(0, 0, 0, 1), new(1, 1, 1)),
        ]);
        await UyaGameplayLayerStore.WriteSourceAsync(project, Source(), Gameplay(
            Tables([]), ShapeBytes(2), [], [], SplineBytes(), sourceCollision, ShapeBytes(1), GrindPathBytes(),
            AreaBytes([new(new(1, 2, 3, 4), 5, [], [0], [], [], [])])));
        var workspace = await ForgeProjectWorkspace.OpenAsync(project);
        var donorCuboid = WithCameraCollision(
            Shape("Donor cuboid", "cuboids", "cuboids", 0, 4, new(10, 20, 30)), 21);
        var donorSphere = Shape("Donor sphere", "spheres", "spheres", 0, 4, new(40, 50, 60));
        var donorSpline = Spline("Donor spline", 0, 4, [new(100, 101, 102, 103)], 0x51);
        var donorPath = GrindPath("Donor path", 0, 4, [new(200, 201, 202, 203)], 0x61);
        var donorArea = Area("Donor area", 0, 4,
            splines: [Link(0, ProjectReferences.AreaSplines, ProjectEntityKind.Spline, donorSpline.EntityId)],
            cuboids: [Link(0, ProjectReferences.AreaCuboids, ProjectEntityKind.Cuboid, donorCuboid.EntityId)],
            spheres: [Link(0, ProjectReferences.AreaSpheres, ProjectEntityKind.Sphere, donorSphere.EntityId)],
            cylinders: [Link(2, ProjectReferences.AreaCylinders, ProjectEntityKind.Cylinder, null)]);
        var cuboids = FamilyPlan("cuboids", ContentImportMode.Replace,
            [$"entity:{removedCuboid.EntityId}"], [$"entity:{retainedCuboid.EntityId}"]);
        var spheres = FamilyPlan("spheres", ContentImportMode.Add, [], []);
        var splines = FamilyPlan("splines", ContentImportMode.Replace,
            [$"entity:{targetSpline.EntityId}"], []);
        var paths = FamilyPlan("paths", ContentImportMode.Add, [], []);
        var areas = FamilyPlan("areas", ContentImportMode.Add, [], []);
        var areaRequirement = new ContentImportReferenceRequirement(
            "areas", 0, ProjectReferences.AreaCylinders, 2, ProjectEntityKind.Cylinder, true, []);
        var plan = new ContentImportPlan(
            ContentImportPlanSchema.CurrentVersion, ContentImportCapabilitySchema.CurrentVersion,
            "import-" + new string('6', 32), "UYA", "NTSC-U", "1.00", new string('a', 32),
            new string('b', 64), 4, workspace.Manifest.ProjectId.ToString(), workspace.CurrentFingerprint,
            3, 0, [cuboids, spheres, splines, paths, areas], [], [], [areaRequirement]);
        var areaChoices = new[]
        {
            new UyaMobyExternalReferenceChoice(
                0, ProjectReferences.AreaCylinders, 2, UyaMobyExternalReferenceAction.Clear),
        };
        var first = UyaGeometryContentImportService.PrepareCommit(
            workspace, [donorCuboid, donorSphere, donorSpline, donorPath, donorArea], plan, 1234,
            referenceChoices: areaChoices);
        var repeated = UyaGeometryContentImportService.PrepareCommit(
            workspace, [donorCuboid, donorSphere, donorSpline, donorPath, donorArea], plan, 1234,
            referenceChoices: areaChoices);
        Equal(true, ForgeProjectPersistence.Serialize(first.Content).AsSpan()
                .SequenceEqual(ForgeProjectPersistence.Serialize(repeated.Content)),
            "shape import IDs and ordering are deterministic");
        await workspace.CommitContentImportAsync(first);
        Equal(true, workspace.Content.Entities.Any(value => value.EntityId == retainedCuboid.EntityId)
            && workspace.Content.Entities.All(value => value.EntityId != removedCuboid.EntityId),
            "cuboid replace retains protected content and removes reviewed content");
        Equal(2, workspace.Content.Entities.Count(value => value.Geometry?.Sphere is not null),
            "sphere add preserves the target and appends the donor shape");
        Equal(1, workspace.Content.Entities.Count(value => value.Geometry?.Spline is not null),
            "spline replace swaps the reviewed target path");
        Equal(2, workspace.Content.Entities.Count(value => value.Geometry?.GrindPath is not null),
            "grind-path add preserves the target and appends the donor path");
        var importedArea = workspace.Content.Entities.Single(value => value.Name == "Imported Donor area").Geometry!.Area!;
        Equal(UyaGeometryContentImportService.ImportedEntityId(plan.BatchId, "splines", 0),
            importedArea.Splines.Single().Reference!.EntityId, "area remaps imported spline link");
        Equal(UyaGeometryContentImportService.ImportedEntityId(plan.BatchId, "cuboids", 0),
            importedArea.Cuboids.Single().Reference!.EntityId, "area remaps imported cuboid link");
        Equal(UyaGeometryContentImportService.ImportedEntityId(plan.BatchId, "spheres", 0),
            importedArea.Spheres.Single().Reference!.EntityId, "area remaps imported sphere link");
        Equal(0, importedArea.Cylinders.Count, "area applies explicit clear for an external link");
        await workspace.SaveAsync();

        var input = await UyaGameplayLayerStore.CreateBakeInputAsync(project);
        Equal(0, input.Blockers!.Count, "imported shape sections are bakeable without donor media");
        var bakePlan = BakeLayerGraph.CreatePlan(Context(),
        [
            new(BakeLayerId.Mobys, "mobys"u8.ToArray(), [], ReadOnlyMemory<byte>.Empty),
            input,
        ]);
        var staging = await BakeStagingStore.OpenAsync(project);
        var snapshot = await UyaGameplayLayerStore.StageAsync(
            project, staging, bakePlan.Layers.Single(value => value.Layer == BakeLayerId.Gameplay));
        var output = Path.Combine(staging.RootPath, snapshot.RelativePath, "pvars");
        var rebuiltCuboids = GameplayGeometryReader.ReadCuboids(
            await File.ReadAllBytesAsync(Path.Combine(output, "cuboids.bin")));
        var rebuiltSpheres = GameplayGeometryReader.ReadShapes(
            await File.ReadAllBytesAsync(Path.Combine(output, "spheres.bin")), "sphere");
        var rebuiltSplines = GameplayGeometryReader.ReadSplines(
            await File.ReadAllBytesAsync(Path.Combine(output, "splines.bin")));
        var rebuiltPaths = GameplayGeometryReader.ReadGrindPaths(
            await File.ReadAllBytesAsync(Path.Combine(output, "grind_splines.bin")));
        var rebuiltAreas = GameplayGeometryReader.ReadAreas(
            await File.ReadAllBytesAsync(Path.Combine(output, "areas.bin")));
        Equal(2, rebuiltCuboids.Length, "cuboid replace output count");
        Equal(1f, rebuiltCuboids[0].Matrix[12], "retained cuboid X");
        Equal(10f, rebuiltCuboids[1].Matrix[12], "imported cuboid X");
        Equal(2, rebuiltSpheres.Length, "sphere add output count");
        Equal(7f, rebuiltSpheres[0].Matrix[12], "retained sphere X");
        Equal(40f, rebuiltSpheres[1].Matrix[12], "imported sphere X");
        Equal(1, rebuiltSplines.Length, "spline replace output count");
        Equal(100f, rebuiltSplines[0].Points[0].X, "imported spline point");
        Equal((byte)0x51, rebuiltSplines[0].NativeHeader[0], "imported spline native metadata");
        Equal(2, rebuiltPaths.Length, "grind-path add output count");
        Equal(200f, rebuiltPaths[1].Points[0].X, "imported grind-path point");
        Equal((byte)0x61, rebuiltPaths[1].NativeHeader[0], "imported grind-path native metadata");
        Equal(2, rebuiltAreas.Length, "area add output count");
        Equal(true, rebuiltAreas[0].CuboidIndices.SequenceEqual([0]), "retained area cuboid link");
        Equal(true, rebuiltAreas[1].SplineIndices.SequenceEqual([0]), "imported area spline link");
        Equal(true, rebuiltAreas[1].CuboidIndices.SequenceEqual([1]), "imported area cuboid link");
        Equal(true, rebuiltAreas[1].SphereIndices.SequenceEqual([1]), "imported area sphere link");
        Equal(0, rebuiltAreas[1].CylinderIndices.Length, "cleared area cylinder link");
        var rebuiltCollision = UyaCameraCollisionGridReader.Read(
            await File.ReadAllBytesAsync(Path.Combine(output, "camera_collision_grid.bin")));
        Equal("0:11,1:21", string.Join(',', rebuiltCollision.Primitives.Select(value =>
                $"{value.Index}:{value.IntValue}")),
            "shape replacement remaps retained and imported camera-collision entries");
    }

    private static ContentImportFamilyPlan FamilyPlan(
        string family,
        ContentImportMode mode,
        IReadOnlyList<string> remove,
        IReadOnlyList<string> protect) => new(
        family, family, ContentImportMode.AddAndReplace,
        mode, ContentImportSourceState.Present, true, false, true,
        [], [], [], remove, protect, [], [], 0, [], []);

    private static ProjectEntity Shape(
        string name,
        string layer,
        string section,
        int index,
        int level,
        ProjectVector3 position)
    {
        var matrix = new float[16];
        matrix[0] = matrix[5] = matrix[10] = matrix[15] = 1;
        matrix[12] = position.X;
        matrix[13] = position.Y;
        matrix[14] = position.Z;
        var inverse = new float[12];
        inverse[0] = inverse[5] = inverse[10] = 1;
        var shape = new ProjectShapeGeometry(matrix, inverse, new(0, 0, 0));
        return new(EntityId.New(), name, layer,
            ProjectTransform.Identity with { Position = position }, null,
            new("UYA", level, $"gameplay/core/{section}", index),
            Geometry: layer == "cuboids" ? new(Cuboid: shape) : new(Sphere: shape));
    }

    private static ProjectEntity WithCameraCollision(ProjectEntity entity, int value) => entity with
    {
        Geometry = new(Cuboid: entity.Geometry!.Cuboid! with
        {
            CameraCollision = new(1, value, value, new(0, 0, 0, 1)),
        }),
    };

    private static byte[] ShapeBytes(int count)
    {
        var bytes = new byte[0x10 + count * 0x80];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, count);
        for (var index = 0; index < count; index++)
        {
            var offset = 0x10 + index * 0x80;
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset), 1);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset + 0x14), 1);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset + 0x28), 1);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset + 0x3c), 1);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset + 0x40), 1);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset + 0x54), 1);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset + 0x68), 1);
        }
        return bytes;
    }

    private static async Task CreateProjectAsync(string path, IReadOnlyList<ProjectEntity> entities) =>
        await ForgeProjectWorkspace.CreateAsync(
            path,
            "Gameplay bake",
            new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
            new("UYA", "NTSC-U", "1.00", 3, new string('a', 32)),
            entities);

    private static ProjectEntity Moby(string name, int pvarIndex, ProjectEntityState state)
    {
        var raw = new byte[UyaMobyInstancesReader.RecordSize];
        BinaryPrimitives.WriteInt32LittleEndian(raw, UyaMobyInstancesReader.RecordSize);
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(0x10), 0x1234);
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(0x28), 0x400);
        BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(0x2c), 1);
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(0x68), pvarIndex);
        return new(
            EntityId.New(),
            name,
            "mobys",
            ProjectTransform.Identity,
            null,
            new("UYA", 3, "gameplay/core/moby_instances", pvarIndex),
            state,
            new(0x400, raw));
    }

    private static ProjectEntity Cuboid()
    {
        var matrix = new float[16];
        matrix[0] = matrix[5] = matrix[10] = matrix[15] = 1;
        var inverse = new float[12];
        inverse[0] = inverse[5] = inverse[10] = 1;
        return new(
            EntityId.New(),
            "Cuboid",
            "cuboids",
            ProjectTransform.Identity with { Position = new(100, 200, 300), Scale = new(2, 3, 4) },
            null,
            new("UYA", 3, "gameplay/core/cuboids", 0),
            Geometry: new(Cuboid: new(matrix, inverse, new(0, 0, 0))));
    }

    private static byte[] CuboidBytes()
    {
        var bytes = new byte[0x90];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, 1);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(0x10), 1);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(0x24), 1);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(0x38), 1);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(0x4c), 1);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(0x50), 1);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(0x64), 1);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(0x78), 1);
        bytes[0x8c] = 0x7f;
        return bytes;
    }

    private static ProjectEntity Camera(byte[] bytes) => new(
        EntityId.New(),
        "Camera",
        "cameras",
        ProjectTransform.Identity with { Position = new(10, 20, 30) },
        null,
        new("UYA", 3, "gameplay/core/cameras", 0),
        Source: new(7, bytes.AsSpan(UyaCameraInstancesReader.HeaderSize).ToArray()),
        Camera: new(-1, new(0, 0, 0)));

    private static byte[] CameraBytes()
    {
        var bytes = new byte[UyaCameraInstancesReader.HeaderSize + UyaCameraInstancesReader.RecordSize];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x10), 7);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x2c), -1);
        return bytes;
    }

    private static ProjectEntity Sound(byte[] bytes)
    {
        var matrix = UyaSoundInstancesReader.Read(bytes).Instances.Single().Matrix;
        return new(
            EntityId.New(),
            "Sound",
            "ambient sounds",
            ProjectTransform.Identity with { Position = new(40, 50, 60), Scale = new(2, 3, 4) },
            null,
            new("UYA", 3, "gameplay/core/sound_instances", 0),
            Source: new(8, bytes.AsSpan(UyaSoundInstancesReader.HeaderSize).ToArray()),
            AmbientSound: new(9, 0x12345678, -1, 64, matrix, new float[12], new(0, 0, 0), 0));
    }

    private static byte[] SoundBytes()
    {
        var bytes = new byte[UyaSoundInstancesReader.HeaderSize + UyaSoundInstancesReader.RecordSize];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(0x10), 8);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(0x12), 9);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x14), 0x12345678);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x18), -1);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(0x1c), 64);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(0x20), 1);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(0x34), 1);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(0x48), 1);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(0x5c), 1);
        return bytes;
    }

    private static ProjectEntity Spline(
        string name,
        int index,
        int level,
        IReadOnlyList<ProjectVector4> points,
        byte metadata) => new(
        EntityId.New(),
        name,
        "splines",
        ProjectTransform.Identity,
        null,
        new("UYA", level, "gameplay/core/splines", index),
        Geometry: new(Spline: new(points, Enumerable.Repeat(metadata, 0x0c).ToArray())));

    private static ProjectEntity Spline() => Spline(
        "Spline", 0, 3, [new(1, 2, 3, 9), new(5, 6, 7, 10), new(9, 10, 11, 12)], 0x7f)
        with { Transform = ProjectTransform.Identity with { Position = new(10, 20, 30), Scale = new(2, 3, 4) } };

    private static ProjectEntity GrindPath(
        string name,
        int index,
        int level,
        IReadOnlyList<ProjectVector4> points,
        byte metadata)
    {
        var record = new byte[0x20];
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(0x10), 11);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(0x14), 1);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(0x18), 2);
        return new(
            EntityId.New(), name, "grind paths", ProjectTransform.Identity, null,
            new("UYA", level, "gameplay/core/grind_splines", index),
            Geometry: new(GrindPath: new(
                new(0, 0, 0, 0), 11, 1, 2, points, record,
                Enumerable.Repeat(metadata, 0x0c).ToArray())));
    }

    private static ProjectEntity Area(
        string name,
        int index,
        int level,
        IReadOnlyList<ProjectGeometryLink>? splines = null,
        IReadOnlyList<ProjectGeometryLink>? cuboids = null,
        IReadOnlyList<ProjectGeometryLink>? spheres = null,
        IReadOnlyList<ProjectGeometryLink>? cylinders = null,
        IReadOnlyList<ProjectGeometryLink>? negativeCuboids = null) => new(
        EntityId.New(), name, "areas", ProjectTransform.Identity, null,
        new("UYA", level, "gameplay/core/areas", index),
        Geometry: new(Area: new(new(10, 20, 30, 40), 7,
            splines ?? [], cuboids ?? [], spheres ?? [], cylinders ?? [], negativeCuboids ?? [])));

    private static ProjectGeometryLink Link(
        int sourceIndex,
        string fieldKey,
        ProjectEntityKind kind,
        EntityId? target) => new(sourceIndex, Reference: new(
        ProjectReferenceDomain.Entity, fieldKey, true, EntityKind: kind, EntityId: target));

    private static byte[] AreaBytes(IReadOnlyList<UyaAreaInstanceComposition> values) =>
        UyaAreaInstancesWriter.Compose([], values);

    private static byte[] SplineBytes()
    {
        var bytes = new byte[0x50];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 0x20);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), 0x30);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x10), 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x20), 2);
        for (var index = 0; index < 8; index++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(0x30 + index * 4), index + 1);
        bytes[0x2c] = 0x7f;
        return bytes;
    }

    private static byte[] GrindPathBytes()
    {
        var bytes = new byte[0x60];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 0x40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), 0x20);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x20), 11);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x24), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x28), 2);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x30), 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x40), 1);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(0x50), 5);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(0x54), 6);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(0x58), 7);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(0x5c), 8);
        bytes[0x44] = 0x41;
        return bytes;
    }

    private static UyaGameplayBlocks Gameplay(
        GameplayPvarTables tables,
        byte[] cuboids,
        byte[] cameras,
        byte[] sounds,
        byte[]? splines = null,
        byte[]? cameraCollision = null,
        byte[]? spheres = null,
        byte[]? grindPaths = null,
        byte[]? areas = null)
    {
        var values = new (string Name, byte[] Bytes)[]
        {
            ("pvar_moby_links", tables.MobyLinksBytes),
            ("pvar_table", tables.TableBytes),
            ("pvar_data", tables.DataBytes),
            ("pvar_relative_pointers", tables.RelativePointerBytes),
            ("cameras", cameras),
            ("sound_instances", sounds),
            ("cuboids", cuboids),
            ("spheres", spheres ?? []),
            ("splines", splines ?? []),
            ("grind_splines", grindPaths ?? []),
            ("areas", areas ?? []),
            ("camera_collision_grid", cameraCollision ?? []),
        };
        return new(
            "core",
            UyaGameplayLayout.CoreHeaderSize,
            new byte[UyaGameplayLayout.CoreHeaderSize],
            tables,
            values.Select((value, index) => new UyaGameplayBlock(
                index, index * 4, 0, value.Name, value.Bytes)).ToArray(),
            new([], [], [], [], [], [], []),
            new([], new([], [], [], true), [], [], []));
    }

    private static GameplayPvarTables Tables(byte[] links) => new(
        links,
        [0, 0, 0, 0, 4, 0, 0, 0],
        [0xde, 0xad, 0xbe, 0xef],
        [0, 0, 0, 0, 0, 0, 0, 0],
        [],
        []);

    private static OpaqueContentSource Source() =>
        new("UYA", "NTSC-U", "1.00", 3, new string('a', 32));

    private static BakeFingerprintContext Context() =>
        new(new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"), "translator-1", "baker-1");

    private static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected {expected}, got {actual}");
    }
}
