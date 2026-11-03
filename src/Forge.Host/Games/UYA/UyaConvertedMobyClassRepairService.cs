using Forge.Host.Domain;
using RatchetPs2.Core.Games;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.UYA;

internal static class UyaConvertedMobyClassRepairService
{
    public static async Task<EditorDiagnostic?> HydrateAsync(ForgeProjectWorkspace workspace, CancellationToken token)
    {
        if (workspace.Manifest.Target.Game != "UYA") return null;
        var original = workspace.Content;
        var content = original;
        var repaired = 0;
        var warnings = new List<string>();
        foreach (var allocation in original.CustomClasses.Allocations.Where(value => value.Asset.Kind == AssetKind.Moby
            && value.Provenance.Origin == ProjectCustomClassOrigin.ConvertedAsset
            && value.ClassId != value.Provenance.SourceClassId))
        {
            token.ThrowIfCancellationRequested();
            var sourceClass = allocation.Provenance.SourceClassId!.Value;
            var instances = content.Entities.Where(value => value.Asset == allocation.Asset).ToArray();
            if (content.Entities.Any(value => value.Layer == "mobys" && value.Source?.ClassId == sourceClass
                    && value.Asset != allocation.Asset)
                || original.CustomClasses.Allocations.Any(value => value.Asset.Kind == AssetKind.Moby
                    && value.Asset != allocation.Asset && (value.ClassId == sourceClass
                        || value.Provenance is { Origin: ProjectCustomClassOrigin.ConvertedAsset }
                            && value.Provenance.SourceClassId == sourceClass))
                || content.Sounds.MobyRemaps.Any(value => value.ClassId == sourceClass))
            {
                warnings.Add($"DL Moby class 0x{sourceClass:X4} conflicts with another project asset; its existing state was preserved.");
                continue;
            }
            if (instances.Any(value => value.Source?.ClassId != allocation.ClassId))
            {
                warnings.Add($"DL Moby class 0x{sourceClass:X4} has independently changed instance classes; its existing state was preserved.");
                continue;
            }
            var source = CatalogModelCodec.Decode(await workspace.ReadAttachedAssetVerifiedAsync(
                allocation.Asset, ForgeProjectWorkspace.MaxAttachedAssetBytes, token));
            if (source.Game != GameId.DL) continue;
            var replacements = instances.Select(value => value with
            {
                Name = value.Name == $"moby:0x{allocation.ClassId:X4}" ? $"moby:0x{sourceClass:X4}" : value.Name,
                Source = value.Source! with { ClassId = sourceClass, RawRecord = GameplayInstanceConverter.RebaseMobyClass(
                    GameId.UYA, value.Source.RawRecord, allocation.ClassId, sourceClass) },
            }).ToDictionary(value => value.EntityId);
            content = content with
            {
                Entities = content.Entities.Select(value => replacements.GetValueOrDefault(value.EntityId) ?? value).ToArray(),
                CustomClasses = content.CustomClasses with { Allocations = content.CustomClasses.Allocations.Select(value =>
                    value.Asset == allocation.Asset ? value with { ClassId = sourceClass } : value).ToArray() },
                Sounds = content.Sounds with { MobyRemaps = content.Sounds.MobyRemaps.Select(value =>
                    value.ClassId == allocation.ClassId ? value with { ClassId = sourceClass } : value).ToArray() },
            };
            repaired++;
        }
        if (repaired > 0) await workspace.CommitAssetsAsync(content, [], token);
        return repaired == 0 && warnings.Count == 0 ? null : new("dl-moby.classes-restored",
            warnings.Count == 0 ? EditorDiagnosticSeverity.Info : EditorDiagnosticSeverity.Warning,
            $"Restored source oClass IDs for {repaired} converted DL Moby class(es). Save the project and rebuild the map. "
            + string.Join(' ', warnings));
    }
}
