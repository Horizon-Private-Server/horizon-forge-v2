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
            await UyaGameplayLayerStore.WriteSourceAsync(
                collisionProject, Source(), Gameplay(Tables([]), CuboidBytes(), [], []));
            var collisionInput = await UyaGameplayLayerStore.CreateBakeInputAsync(collisionProject);
            Equal(true, collisionInput.Blockers!.Any(value => value.Contains(
                "camera-collision shape", StringComparison.Ordinal)), "moved camera-collision shape blocks serialization");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task CreateProjectAsync(string path, IReadOnlyList<ProjectEntity> entities) =>
        await ForgeProjectWorkspace.CreateAsync(
            path,
            "Gameplay bake",
            new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
            new("UYA", "NTSC-U", "1.00", 3, new string('a', 32), EntityVersion: 1),
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

    private static ProjectEntity Spline() => new(
        EntityId.New(),
        "Spline",
        "splines",
        ProjectTransform.Identity with { Position = new(10, 20, 30), Scale = new(2, 3, 4) },
        null,
        new("UYA", 3, "gameplay/core/splines", 0),
        Geometry: new(Spline: new([new(1, 2, 3, 9), new(5, 6, 7, 10), new(9, 10, 11, 12)])));

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

    private static UyaGameplayBlocks Gameplay(
        GameplayPvarTables tables,
        byte[] cuboids,
        byte[] cameras,
        byte[] sounds,
        byte[]? splines = null)
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
            ("splines", splines ?? []),
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
