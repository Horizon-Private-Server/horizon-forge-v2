using System.Security.Cryptography;
using System.Text.Json;
using Forge.Host.Domain;
using Forge.Host.Games.UYA;
using RatchetPs2.Core.Games;
using RatchetPs2.Games.UYA.Gameplay;
using RatchetPs2.Sdk;

namespace Forge.Host.ProtocolTests.Games.RC1;

internal static class Rc1ContentImportQualificationTests
{
    public static async Task QualifyAsync(string isoPath, string catalogRoot, int? sourceLevel = null)
    {
        var identity = await IsoSourceService.ValidateAsync(GameId.RC1, isoPath);
        ContentImportSourceCache.ValidateIdentity(identity, GameId.RC1, "NTSC-U", "1.00", identity.Fingerprint);
        var root = Directory.CreateTempSubdirectory("forge-rc1-content-qualification-").FullName;
        try
        {
            var workspace = await ForgeProjectWorkspace.CreateAsync(Path.Combine(root, "project"), "RC1 qualification",
                new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"), new("UYA", "NTSC-U", "1.00", 40, new string('a', 32)), []);
            await using var iso = File.OpenRead(isoPath);
            var dex = new MobyDexCatalog(UyaMobyDexDataset.Create());
            var failures = new List<string>();
            foreach (var level in LevelCatalogReader.FindAvailable(GameId.RC1, iso))
            {
                if (sourceLevel is not null && level != sourceLevel) continue;
                try
                {
                    var source = await ContentImportSourceCache.ReadAsync(GameId.RC1, GameId.UYA, isoPath,
                        identity.Fingerprint, level, "NTSC-U", "1.00", null, default);
                    var cached = await ContentImportSourceCache.ReadAsync(GameId.RC1, GameId.UYA, isoPath,
                        identity.Fingerprint, level, "NTSC-U", "1.00", null, default);
                    if (!ReferenceEquals(source, cached) || source.Conversion is null)
                        throw new InvalidDataException("The normalized donor conversion was not reused.");
                    var inventory = source.Families;
                    var request = new UyaConvertedContentImportPlanRequest(GameId.RC1, workspace.RootPath, catalogRoot, isoPath, identity.Fingerprint, level, []);
                    var prepared = await UyaConvertedContentImportPreparation.CreateAsync(request, source, dex, null, default);
                    var plan = UyaConvertedContentImportService.CreatePlan(request, workspace, source, prepared.Source, prepared.Diagnostics);
                    var nativeInventory = UyaContentImportPlanningService.Inventory(prepared.Source);
                    foreach (var rawFamily in inventory.Where(family => family.Family != "opaqueContent"))
                    {
                        var nativeFamily = nativeInventory.Single(family => family.Family == rawFamily.Family);
                        var expectedCount = rawFamily.Items.Sum(item => item.ItemCount);
                        if (rawFamily.Family == "ties")
                            expectedCount += rawFamily.Items.Sum(item => item.ClassId is { } id
                                && prepared.SplitTieClasses.TryGetValue(id, out var parts) ? item.ItemCount * (parts.Count - 1) : 0);
                        if (rawFamily.Diagnostic is not null || prepared.Diagnostics.ContainsKey(rawFamily.Family)
                            || expectedCount != nativeFamily.Items.Sum(item => item.ItemCount)
                            || rawFamily.Items.Count > 0 && nativeFamily.State != ContentImportSourceState.Present)
                            failures.Add($"RC1 level {level} {rawFamily.Family} does not match the donor inventory.");
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
                    failures.Add($"RC1 level {level}: {cause.Message}");
                    Console.WriteLine(JsonSerializer.Serialize(new { level, error = cause.ToString() }));
                }
            }
            if (failures.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, failures));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
