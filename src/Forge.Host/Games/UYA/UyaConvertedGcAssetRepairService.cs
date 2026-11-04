using Forge.Host.Domain;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.LevelAssets;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.UYA;

internal static class UyaConvertedGcAssetRepairService
{
    public static async Task<EditorDiagnostic?> HydrateAsync(ForgeProjectWorkspace workspace, CancellationToken token)
    {
        if (workspace.Manifest.Target.Game != "UYA") return null;
        var original = workspace.Content;
        var content = original;
        var attachments = new ProjectAssetAttachmentCollector();
        var warnings = new List<string>();
        var repaired = 0;
        foreach (var allocation in original.CustomClasses.Allocations.Where(value =>
            value.Asset.Kind is (AssetKind.Tie or AssetKind.Shrub)
            && value.Provenance.Origin == ProjectCustomClassOrigin.ConvertedAsset))
        {
            token.ThrowIfCancellationRequested();
            var source = CatalogModelCodec.Decode(await workspace.ReadAttachedAssetVerifiedAsync(
                allocation.Asset, ForgeProjectWorkspace.MaxAttachedAssetBytes, token));
            if (source.Game != GameId.GC) continue;
            var sourceClass = allocation.Provenance.SourceClassId!.Value;
            var description = $"GC {allocation.Asset.Kind} 0x{sourceClass:X4}";
            var instances = content.Entities.Where(value => value.Asset == allocation.Asset).ToArray();
            var layer = allocation.Asset.Kind == AssetKind.Tie ? "ties" : "shrubs";
            if (content.Entities.Any(value => value.Layer == layer && value.Source?.ClassId == sourceClass
                    && value.Asset != allocation.Asset)
                || original.CustomClasses.Allocations.Any(value => value.Asset.Kind == allocation.Asset.Kind
                    && value.Asset != allocation.Asset && (value.ClassId == sourceClass
                        || value.Provenance.Origin == ProjectCustomClassOrigin.ConvertedAsset
                            && value.Provenance.SourceClassId == sourceClass)))
            {
                warnings.Add($"{description} conflicts with another project asset; its existing state was preserved.");
                continue;
            }
            var binding = content.AssetOverrides.SingleOrDefault(value => value.Source == allocation.Asset);
            if (binding is null || content.Assets.Single(value => value.Id == binding.Replacement.Id).ParentId != allocation.Asset.Id
                || instances.Any(value => value.Source?.ClassId != allocation.ClassId))
            {
                warnings.Add($"{description} has independently changed models or instance classes; its existing state was preserved.");
                continue;
            }
            var native = UyaCanonicalAssetCodec.Decode(await workspace.ReadAttachedAssetVerifiedAsync(
                binding.Replacement, ForgeProjectWorkspace.MaxAttachedAssetBytes, token));
            var kind = allocation.Asset.Kind == AssetKind.Tie ? FrontendAssetKind.Tie : FrontendAssetKind.Shrub;
            byte[]? model;
            try
            {
                model = await Task.Run(() => LevelAssetModelConverter.RepairPreviousStaticConversion(
                    GameId.GC, GameId.UYA, kind, source.ModelBytes, native.ModelBytes,
                    allocation.ClassId, sourceClass, token), token);
            }
            catch (NotSupportedException exception)
            {
                warnings.Add($"{description} could not be repaired ({exception.Message}); its existing state was preserved.");
                continue;
            }
            if (model is null)
            {
                warnings.Add($"{description} has an independently changed model; its existing state was preserved.");
                continue;
            }
            if (sourceClass == allocation.ClassId && model.AsSpan().SequenceEqual(native.ModelBytes)) continue;
            var bytes = UyaCanonicalAssetCodec.Encode(native.DefinitionBytes, model, native.Textures);
            var id = AssetId.Compute(allocation.Asset.Kind, UyaAssetImportService.CanonicalFormatVersion, bytes);
            var existing = content.Assets.SingleOrDefault(value => value.Id == id);
            var replacement = attachments.Attach(allocation.Asset.Kind, UyaAssetImportService.CanonicalFormatVersion,
                bytes, existing is null ? allocation.Asset.Id : existing.ParentId);
            var entities = instances.Select(value => value with
            {
                Name = value.Name == $"{allocation.Asset.Kind.ToString().ToLowerInvariant()}:0x{allocation.ClassId:X4}"
                    ? $"{allocation.Asset.Kind.ToString().ToLowerInvariant()}:0x{sourceClass:X4}" : value.Name,
                Source = value.Source! with { ClassId = sourceClass, RawRecord = GameplayInstanceConverter.RebaseStaticClass(
                    GameId.UYA, kind, value.Source.RawRecord, allocation.ClassId, sourceClass) },
            }).ToDictionary(value => value.EntityId);
            content = content with
            {
                Entities = content.Entities.Select(value => entities.GetValueOrDefault(value.EntityId) ?? value).ToArray(),
                CustomClasses = content.CustomClasses with { Allocations = content.CustomClasses.Allocations.Select(value =>
                    value.Asset == allocation.Asset ? value with { ClassId = sourceClass } : value).ToArray() },
                AssetOverrides = content.AssetOverrides.Select(value => value == binding
                    ? value with { Replacement = replacement } : value).ToArray(),
            };
            repaired++;
        }
        if (repaired > 0) await workspace.CommitAssetsAsync(content, attachments.Attachments, token);
        return repaired == 0 && warnings.Count == 0 ? null : new("gc-assets.conversion-repaired",
            warnings.Count == 0 ? EditorDiagnosticSeverity.Info : EditorDiagnosticSeverity.Warning,
            $"Repaired conversion and source class IDs for {repaired} GC asset(s). Save the project and rebuild occlusion and the map. "
            + string.Join(' ', warnings));
    }
}
