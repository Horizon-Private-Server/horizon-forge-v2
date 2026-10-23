using System.Buffers.Binary;
using Forge.Host.Domain;
using Forge.Host.Games.UYA;
using RatchetPs2.Games.UYA.Gameplay;

namespace Forge.Host.ProtocolTests.Games.UYA;

internal static class UyaDirectionalLightsContentImportTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-directional-light-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var catalog = await AssetCatalogStore.OpenAsync(Path.Combine(root, "catalog"));
            var targetFingerprint = new string('a', 32);
            var source = new OpaqueContentSource("UYA", "NTSC-U", "1.00", 41, targetFingerprint);
            var payloads = new[]
            {
                new UyaBaseLayerPayload(BakeLayerId.Lighting, "directional-lights.bin", AssetKind.Lighting,
                    "gameplay/core/directional_lights.bin", 0, DirectionalBytes(1)),
                new UyaBaseLayerPayload(BakeLayerId.Lighting, "point-lights.bin", AssetKind.Lighting,
                    "gameplay/core/point_lights.bin", 0, [0]),
                new UyaBaseLayerPayload(BakeLayerId.Lighting, "tie-ambient-rgbas.bin", AssetKind.Lighting,
                    "gameplay/core/tie_ambient_rgbas.bin", 0, [0]),
            };
            var entries = await catalog.PutManyAsync(
                UyaBaseLayerService.CreateCatalogPuts(payloads, source, "target-lighting"));
            var targetId = EntityId.New();
            var tie = new ProjectEntity(
                EntityId.New(), "Retained TIE", "ties", ProjectTransform.Identity, null,
                new("UYA", 41, "gameplay/core/tie_instances", 0),
                Source: new(1, new byte[UyaTieInstancesReader.RecordSize]),
                TieLighting: new(0, []));
            var projectPath = Path.Combine(root, "project");
            var workspace = await ForgeProjectWorkspace.CreateAsync(
                projectPath,
                "Directional light import",
                new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
                new("UYA", "NTSC-U", "1.00", 41, targetFingerprint),
                [Light(targetId, 0, 41, 1), tie]);
            await UyaBaseLayerStore.WriteAsync(projectPath, source, payloads, entries);

            var batchId = $"import-{new string('c', 32)}";
            var family = new ContentImportFamilyPlan(
                "directionalLights", "Directional lights", ContentImportMode.Replace,
                ContentImportMode.Replace, ContentImportSourceState.Present,
                true, false, true,
                ["source:directionalLights:0", "source:directionalLights:1"], [], [],
                [$"entity:{targetId}"], [], [], [], 128, [], []);
            var plan = new ContentImportPlan(
                ContentImportPlanSchema.CurrentVersion,
                ContentImportCapabilitySchema.CurrentVersion,
                batchId,
                "UYA",
                "NTSC-U",
                "1.00",
                new string('b', 32),
                new string('d', 64),
                40,
                workspace.Manifest.ProjectId.ToString(),
                workspace.CurrentFingerprint,
                41,
                128,
                [family],
                [],
                []);
            var donor = new[] { Light(EntityId.New(), 0, 40, 10), Light(EntityId.New(), 1, 40, 20) };
            var command = await UyaDirectionalLightsContentImportService.PrepareCommitAsync(
                workspace, catalog, donor, plan, 1234);
            var imported = command.Content.Entities.Where(value => value.Lighting?.DirectionalLight is not null)
                .OrderBy(value => value.Provenance!.SourceIndex).ToArray();
            Equal(2, imported.Length, "donor light count replaces target count");
            Equal(20f, imported[1].Lighting!.DirectionalLight!.TopColor.X,
                "donor directional-light values become authoritative");
            Equal(true, command.Batch.Replaced.Contains($"entity:{targetId}"),
                "target light is recorded as replaced");
            Equal(true, UyaDirectionalLightsContentImportService.SelectorFits(0x110, 2),
                "blended selector fits two lights");
            Equal(false, UyaDirectionalLightsContentImportService.SelectorFits(0x110, 1),
                "blended selector requires its secondary light");
            Equal(true, UyaDirectionalLightsContentImportService.SelectorFits(0x0f, 1),
                "retail no-light sentinel does not require a directional-light slot");

            await workspace.CommitContentImportAsync(command);
            Equal(2, workspace.Content.Entities.Count(value => value.Lighting?.DirectionalLight is not null),
                "directional-light replacement commits atomically");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ProjectEntity Light(EntityId id, int sourceIndex, int level, float value) => new(
        id,
        $"Directional Light #{sourceIndex}",
        "directional lights",
        ProjectTransform.Identity,
        null,
        new("UYA", level, "gameplay/core/directional_lights", sourceIndex),
        Lighting: new(DirectionalLight: new(
            new(value, 2, 3, 4), new(5, 6, 7, 8),
            new(9, 10, 11, 12), new(13, 14, 15, 16))));

    private static byte[] DirectionalBytes(int count)
    {
        var bytes = new byte[0x10 + count * 0x40];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, count);
        return bytes;
    }

    private static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected {expected}, got {actual}");
    }
}
