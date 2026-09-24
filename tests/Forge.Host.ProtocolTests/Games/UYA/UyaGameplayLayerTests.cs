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
            await CreateProjectAsync(project, [moby]);
            var tables = Tables([]);
            await UyaGameplayLayerStore.WriteSourceAsync(project, Source(), tables);

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
            Equal(true, (await File.ReadAllBytesAsync(Path.Combine(output, "pvars", "pvar_data.bin")))
                .SequenceEqual(tables.DataBytes), "pvar data preserved");
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
