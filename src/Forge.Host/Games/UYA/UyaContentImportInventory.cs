using Forge.Host.Domain;
using RatchetPs2.Games.UYA.Gameplay;

namespace Forge.Host.Games.UYA;

internal static partial class UyaContentImportPlanningService
{
    internal static IReadOnlyList<UyaContentImportSourceFamily> Inventory(UyaBaseLevelData source)
    {
        var items = source.Entities
            .Select(entity => (Family: Family(entity), Entity: entity))
            .Where(value => value.Family is not null)
            .Select(value => (Family: value.Family!, Item: SourceItem(value.Entity, value.Family!)))
            .GroupBy(value => value.Family!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(value => value.Item).ToArray(), StringComparer.Ordinal);
        items["tfrags"] = source.Tfrags.State.Sources.Where(value => value.GroupCount > 0)
            .Select(value => new UyaContentImportSourceItem(
                $"source:tfrags:{value.SourceKind.ToString().ToLowerInvariant()}:{value.SourceIndex}",
                ItemCount: value.GroupCount))
            .ToArray();
        var families = new List<UyaContentImportSourceFamily>(FamilyOrder.Length);
        foreach (var family in FamilyOrder)
        {
            var familyItems = items.GetValueOrDefault(family) ?? [];
            var estimate = EstimateBytes(source, family);
            string? diagnostic = null;
            if (family == "mobys")
            {
                var blockers = UyaMobyContentImportService.CapabilityBlockers(source);
                if (blockers.Count > 0)
                {
                    diagnostic = string.Join(' ', blockers);
                    if (diagnostic.Length > 512) diagnostic = diagnostic[..512];
                }
            }
            else if (family == "levelSettings" && source.LevelSettings is not null)
                familyItems = [new("source:levelSettings:singleton")];
            else if (family == "mapGroups")
            {
                try
                {
                    familyItems = MapGroups(source.OpaqueSections, source.Entities);
                }
                catch (Exception exception) when (exception is InvalidDataException or OverflowException)
                {
                    familyItems = [];
                    diagnostic = $"Map-group inventory is malformed ({exception.Message}).";
                    if (diagnostic.Length > 512) diagnostic = diagnostic[..512];
                }
            }
            else if (family == "hud" && source.Hud is not null)
                familyItems = [new("source:hud:singleton")];
            else if (family == "fx" && source.Fx is not null)
            {
                if (source.Fx.SourceTextures.Any(value => value.Texture is null || value.Diagnostic is not null))
                {
                    diagnostic = "The donor FX bank contains one or more unreadable textures.";
                    familyItems = [];
                }
                else if (source.Fx.SourceTextures.Count > 0)
                    familyItems = [new("source:fx:singleton")];
            }
            else if (family == "occlusion" && source.Occlusion is not null)
            {
                try
                {
                    ValidateOcclusionSource(source);
                    familyItems = [new("source:occlusion:singleton")];
                }
                catch (Exception exception) when (exception is InvalidDataException or OverflowException)
                {
                    familyItems = [];
                    diagnostic = $"Occlusion inventory is malformed ({exception.Message}).";
                    if (diagnostic.Length > 512) diagnostic = diagnostic[..512];
                }
            }
            else if (family == "opaqueContent" && estimate > 0)
                familyItems = [new("source:opaqueContent:payload")];
            var present = SourceSectionPresent(source, family);
            families.Add(new(family,
                diagnostic is not null ? ContentImportSourceState.Malformed
                    : familyItems.Length > 0 ? ContentImportSourceState.Present
                    : present ? ContentImportSourceState.Empty : ContentImportSourceState.Absent,
                familyItems,
                estimate,
                diagnostic));
        }
        return families;
    }

    private static void ValidateOcclusionSource(UyaBaseLevelData source)
    {
        var occlusion = source.Occlusion
            ?? throw new InvalidDataException("The donor occlusion payload is absent.");
        var mappings = UyaOcclusionMappingsReader.Read(occlusion.GameplayMappings);
        var tieCount = source.Entities.Count(value => value.Layer == "ties");
        var mobyCount = source.Entities.Count(value => value.Layer == "mobys");
        var tfragCount = source.Tfrags.State.Sources.SingleOrDefault(value =>
            value.SourceKind == ProjectTfragSourceKind.Primary && value.SourceIndex == 0)?.GroupCount ?? 0;
        if (mappings.Ties.Count != tieCount || mappings.Mobys.Count != mobyCount
            || mappings.Tfrags.Count != tfragCount)
            throw new InvalidDataException(
                "Gameplay mappings do not match the donor TIE, Moby, and primary tfrag inventories.");
    }

    private static UyaContentImportSourceItem[] MapGroups(
        IReadOnlyList<OpaqueSectionCapture> sections,
        IReadOnlyList<ProjectEntity> entities)
    {
        var result = new List<UyaContentImportSourceItem>();
        foreach (var (sectionName, prefix, memberSection) in new[]
        {
            ("gameplay/moby_groups", "moby", "gameplay/core/moby_instances"),
            ("gameplay/tie_groups", "tie", "gameplay/core/tie_instances"),
            ("gameplay/shrub_groups", "shrub", "gameplay/core/shrub_instances"),
        })
        {
            var matching = sections.Where(value => value.Name == sectionName).ToArray();
            if (matching.Length > 1) throw new InvalidDataException($"Duplicate {prefix} map-group sections.");
            if (matching.Length == 0) continue;
            var groups = UyaTieGroupsReader.Read(matching[0].Bytes).Groups;
            var sourceIndices = entities.Where(value => value.Provenance?.Section == memberSection)
                .Select(value => value.Provenance!.SourceIndex).ToHashSet();
            var missing = groups.SelectMany(value => value).FirstOrDefault(value => !sourceIndices.Contains(value), -1);
            if (missing >= 0)
                throw new InvalidDataException($"The {prefix} map-group table references missing instance {missing}.");
            result.Add(new UyaContentImportSourceItem($"source:mapGroups:{prefix}:table", ItemCount: 0));
            result.AddRange(groups.Select((_, index) =>
                new UyaContentImportSourceItem($"source:mapGroups:{prefix}:{index}")));
        }
        return result.ToArray();
    }

}
