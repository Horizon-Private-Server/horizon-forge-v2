using System.Security.Cryptography;
using System.Text.Json;
using Forge.Host.Domain;
using Forge.Host.Games.UYA;
using RatchetPs2.Core.Games;
using RatchetPs2.Games.UYA.Gameplay;
using RatchetPs2.Sdk;

namespace Forge.Host.ProtocolTests.Games.GC;

internal static class GcContentImportQualificationTests
{
    public static async Task QualifyAsync(string isoPath, string catalogRoot, int? sourceLevel = null)
    {
        var identity = await IsoSourceService.ValidateAsync(GameId.GC, isoPath);
        ContentImportSourceCache.ValidateIdentity(identity, GameId.GC, "NTSC-U", "1.01", identity.Fingerprint);
        var root = Directory.CreateTempSubdirectory("forge-gc-content-qualification-").FullName;
        try
        {
            var workspace = await ForgeProjectWorkspace.CreateAsync(Path.Combine(root, "project"), "GC qualification",
                new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"), new("UYA", "NTSC-U", "1.00", 40, new string('a', 32)), []);
            await using var iso = File.OpenRead(isoPath);
            var dex = new MobyDexCatalog(UyaMobyDexDataset.Create());
            var failures = new List<string>();
            foreach (var level in LevelCatalogReader.FindAvailable(GameId.GC, iso))
            {
                if (sourceLevel is not null && level != sourceLevel) continue;
                try
                {
                    var source = await ContentImportSourceCache.ReadAsync(GameId.GC, GameId.UYA, isoPath,
                        identity.Fingerprint, level, "NTSC-U", "1.01", null, default);
                    var cached = await ContentImportSourceCache.ReadAsync(GameId.GC, GameId.UYA, isoPath,
                        identity.Fingerprint, level, "NTSC-U", "1.01", null, default);
                    if (!ReferenceEquals(source, cached) || source.Conversion is null)
                        throw new InvalidDataException("The normalized donor conversion was not reused.");
                    var inventory = source.Families;
                    var request = new UyaConvertedContentImportPlanRequest(GameId.GC, workspace.RootPath, catalogRoot, isoPath, identity.Fingerprint, level, []);
                    var prepared = await UyaConvertedContentImportPreparation.CreateAsync(request, source, dex, null, default);
                    var plan = UyaConvertedContentImportService.CreatePlan(request, workspace, source, prepared.Source, prepared.Diagnostics);
                    var nativeInventory = UyaContentImportPlanningService.Inventory(prepared.Source);
                    foreach (var rawFamily in inventory.Where(family => family.Family != "opaqueContent"))
                    {
                        var nativeFamily = nativeInventory.Single(family => family.Family == rawFamily.Family);
                        if (rawFamily.Diagnostic is not null || prepared.Diagnostics.ContainsKey(rawFamily.Family)
                            || rawFamily.Items.Sum(item => item.ItemCount) != nativeFamily.Items.Sum(item => item.ItemCount)
                            || rawFamily.Items.Count > 0 && nativeFamily.State != ContentImportSourceState.Present)
                            failures.Add($"GC level {level} {rawFamily.Family} does not match the donor inventory.");
                    }
                    var mappings = prepared.Source.Occlusion is { } occlusion
                        ? UyaOcclusionMappingsReader.Read(occlusion.GameplayMappings) : null;
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        level,
                        conversionDiagnostics = prepared.Diagnostics,
                        mappingCounts = mappings is null ? null : new
                        {
                            ties = mappings.Ties.Count, mobys = mappings.Mobys.Count, tfrags = mappings.Tfrags.Count,
                            terrainGroups = prepared.Source.Tfrags.State.Sources.Select(value => new { value.SourceIndex, value.GroupCount }),
                            terrainMappings = prepared.Source.Tfrags.Entities.GroupBy(value => value.Tfrag!.SourceIndex).Select(group => new
                            {
                                owner = group.Key,
                                stash = group.Select(value => value.Tfrag!.OcclusionIndexStash).Distinct(),
                                first = group.Min(value => value.Tfrag!.OcclusionIndex),
                                last = group.Max(value => value.Tfrag!.OcclusionIndex),
                            }),
                            terrainHashes = prepared.Source.BaseLayers.Where(value => value.Layer == BakeLayerId.Tfrags)
                                .Select(value => new { value.SourceIndex, hash = Convert.ToHexString(SHA256.HashData(value.Bytes)) }),
                        },
                        families = plan.Families.Select(family => new
                        {
                            family.Family, family.SourceState, family.Selectable,
                            rawCount = inventory.Single(value => value.Family == family.Family).Items.Sum(item => item.ItemCount),
                            nativeCount = nativeInventory.Single(value => value.Family == family.Family).Items.Sum(item => item.ItemCount),
                            family.Blockers,
                            diagnostic = nativeInventory
                                .Single(value => value.Family == family.Family).Diagnostic
                                ?? prepared.Diagnostics.GetValueOrDefault(family.Family),
                        }),
                    }));
                }
                catch (Exception cause) when (cause is IOException or InvalidDataException or ArgumentException or NotSupportedException)
                {
                    failures.Add($"GC level {level}: {cause.Message}");
                    Console.WriteLine(JsonSerializer.Serialize(new { level, error = cause.ToString() }));
                }
            }
            if (failures.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, failures));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
