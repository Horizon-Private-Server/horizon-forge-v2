using System.Buffers.Binary;
using System.Security.Cryptography;
using Forge.Host.Domain;
using Forge.Host.Games.GC;
using Forge.Host.Games.UYA;
using RatchetPs2.Core.Games;
using RatchetPs2.Games.UYA.Gameplay;
using RatchetPs2.Sdk;

namespace Forge.Host.ProtocolTests.Games.GC;

internal static class GcLevelSettingsContentImportTests
{
    public static async Task RunAsync()
    {
        var root = Directory.CreateTempSubdirectory("forge-gc-settings-").FullName;
        try
        {
            var catalog = await AssetCatalogStore.OpenAsync(Path.Combine(root, "catalog"));
            var targetSource = new OpaqueContentSource("UYA", "NTSC-U", "1.00", 41, new string('a', 32));
            var payload = new UyaBaseLayerPayload(BakeLayerId.World, "level-settings.bin", AssetKind.World,
                "gameplay/core/level_settings.bin", 0, new byte[UyaLevelSettingsReader.MinimumSize]);
            var entries = await catalog.PutManyAsync(UyaBaseLayerService.CreateCatalogPuts([payload], targetSource, "settings-test"));
            var wad = GcContentImportPreviewTests.CreateWad();
            foreach (var (offset, value) in new[] { (0, 9), (4, 8), (8, 7), (12, 6), (16, 5), (20, 4) })
                BinaryPrimitives.WriteInt32LittleEndian(wad.AsSpan(0x1210 + offset), value);
            var fingerprint = new string('b', 32);
            var source = new ContentImportSource(new(true, "GC", "NTSC-U", "1.01", "SCUS-97268", wad.Length, fingerprint, "fixture"),
                Convert.ToHexString(SHA256.HashData(wad)).ToLowerInvariant(), LevelContentInventoryReader.Read(GameId.GC, wad), wad);
            var dex = new MobyDexCatalog(UyaMobyDexDataset.Create());

            foreach (var replaceCuboids in new[] { false, true })
            {
                var path = Path.Combine(root, replaceCuboids ? "with-cuboids" : "settings-only");
                await UyaBaseLayerStore.WriteAsync(path, targetSource, [payload], entries);
                var selections = new List<ContentImportSelection> { new("levelSettings", ContentImportMode.Replace) };
                if (replaceCuboids) selections.Add(new("cuboids", ContentImportMode.Replace));
                var request = new UyaConvertedContentImportPlanRequest(GameId.GC, path, catalog.RootPath, "fixture.iso", fingerprint, 1, selections);
                var prepared = await UyaConvertedContentImportPreparation.CreateAsync(request, source, dex, null, default);
                var cuboid = prepared.Source.Entities.Single(entity => entity.Geometry?.Cuboid is not null) with
                {
                    EntityId = EntityId.New(), Name = "Target ship camera cuboid",
                    Provenance = new("UYA", 41, "gameplay/core/cuboids", 0),
                };
                var spline = new ProjectEntity(EntityId.New(), "Target ship path", "splines", ProjectTransform.Identity,
                    null, new("UYA", 41, "gameplay/core/splines", 0), Geometry: new(Spline: new([])));
                var settings = new ProjectLevelSettings(new(1, 2, 3), new(4, 5, 6), 10, 100, 255, 0,
                    new(-64, false, new(1, 2, 3), new(4, 5, 6), 0.5f,
                        Reference(ProjectReferences.LevelShipPath, ProjectEntityKind.Spline, spline.EntityId),
                        Reference(ProjectReferences.LevelShipCameraCuboidStart, ProjectEntityKind.Cuboid, cuboid.EntityId),
                        Reference(ProjectReferences.LevelShipCameraCuboidEnd, ProjectEntityKind.Cuboid, cuboid.EntityId)));
                var workspace = await ForgeProjectWorkspace.CreateAsync(path, "GC settings", new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
                    new("UYA", "NTSC-U", "1.00", 41, targetSource.Fingerprint), [cuboid, spline], settings);
                var before = workspace.CurrentFingerprint;
                var plan = UyaConvertedContentImportService.CreatePlan(request, workspace, source, prepared.Source, prepared.Diagnostics);
                Check(plan.Blockers.Count == 0, string.Join(' ', plan.Blockers));
                Check((plan.ReferenceRequirements ?? []).Count == 0, "retained scene links need no donor mapping choices");
                if (replaceCuboids)
                    Check(plan.Families.Single(family => family.Family == "cuboids").Protect.Contains($"entity:{cuboid.EntityId}"),
                        "cuboid replacement protects the retained ship camera target");

                var command = await UyaConvertedContentImportService.PrepareCommitAsync(request, workspace, source, prepared, plan.BatchId, [], dex);
                Check(workspace.CurrentFingerprint == before, "preparation leaves the target unchanged");
                Check(command.Content.LevelSettings!.BackgroundColor == new ProjectRgb24(9, 8, 7)
                    && command.Content.LevelSettings.FogColor == new ProjectRgb24(6, 5, 4), "GC environment settings import");
                Check(command.Content.LevelSettings.Scene == settings.Scene, "target scene and ship links remain intact");
                await workspace.CommitContentImportAsync(command);
                await workspace.SaveAsync();
                var reopened = await ForgeProjectWorkspace.OpenAsync(path);
                Check(reopened.Content.LevelSettings!.Scene == settings.Scene, "retained scene survives save and reopen");
                Check(reopened.Content.Entities.Any(entity => entity.EntityId == cuboid.EntityId), "retained camera cuboid exists");
                await UyaBaseLayerStore.ValidateLevelSettingsAsync(reopened, catalog, default);
            }
            Console.WriteLine("GC environment-settings replacement preserves target scene links with and without cuboid replacement.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static ProjectIndexedReference Reference(string fieldKey, ProjectEntityKind kind, EntityId id) =>
        new(0, new(ProjectReferenceDomain.Entity, fieldKey, true, EntityKind: kind, EntityId: id));

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception($"GC level settings: {message}");
    }
}
