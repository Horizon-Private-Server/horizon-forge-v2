using Forge.Host.Domain;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.Textures.Palettes;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.UYA;

internal static class UyaConvertedAssetRepairService
{
    public static async Task<EditorDiagnostic?> HydrateAsync(
        ForgeProjectWorkspace workspace, CancellationToken cancellationToken)
    {
        if (workspace.Manifest.Target.Game != "UYA") return null;
        var overrides = workspace.Content.AssetOverrides.ToDictionary(value => value.Source);
        var attachments = new ProjectAssetAttachmentCollector();
        var repaired = 0;
        foreach (var allocation in workspace.Content.CustomClasses.Allocations.Where(value =>
            value.Asset.Kind is (AssetKind.Tie or AssetKind.Shrub or AssetKind.Moby)
            && value.Provenance.Origin == ProjectCustomClassOrigin.ConvertedAsset))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!overrides.TryGetValue(allocation.Asset, out var binding)) continue;
            var attached = workspace.Content.Assets.SingleOrDefault(value => value.Id == binding.Replacement.Id);
            if (attached?.ParentId != allocation.Asset.Id) continue;
            var source = CatalogModelCodec.Decode(await workspace.ReadAttachedAssetVerifiedAsync(
                allocation.Asset, ForgeProjectWorkspace.MaxAttachedAssetBytes, cancellationToken));
            if (source.Game != GameId.DL) continue;
            var native = UyaCanonicalAssetCodec.Decode(await workspace.ReadAttachedAssetVerifiedAsync(
                binding.Replacement, ForgeProjectWorkspace.MaxAttachedAssetBytes, cancellationToken));
            var bytes = await Task.Run(() =>
            {
                var model = allocation.Asset.Kind switch
                {
                    AssetKind.Tie => LevelAssetModelConverter.RepairPreviousTieConversion(
                        GameId.DL, GameId.UYA, source.ModelBytes, native.ModelBytes, allocation.ClassId),
                    AssetKind.Moby =>
                        LevelAssetModelConverter.RepairPreviousMobyConversion(GameId.DL, GameId.UYA,
                            source.ModelBytes, native.ModelBytes, cancellationToken),
                    _ => null,
                };
                var textures = native.Textures.ToArray();
                var texturesChanged = false;
                if (source.Textures.Count == textures.Length)
                {
                    for (var index = 0; index < textures.Length; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (source.Textures[index].Role != textures[index].Role) continue;
                        var texture = LevelAssetModelConverter.RepairPreviousTextureConversion(
                            GameId.DL, GameId.UYA, source.Textures[index].PifBytes, textures[index].PifBytes,
                            (TextureRole)textures[index].Role);
                        if (texture is null) continue;
                        textures[index] = textures[index] with { PifBytes = texture };
                        texturesChanged = true;
                    }
                }
                return model is null && !texturesChanged ? null
                    : UyaCanonicalAssetCodec.Encode(native.DefinitionBytes, model ?? native.ModelBytes, textures);
            }, cancellationToken);
            if (bytes is null) continue;
            var id = AssetId.Compute(allocation.Asset.Kind, UyaAssetImportService.CanonicalFormatVersion, bytes);
            var existing = workspace.Content.Assets.SingleOrDefault(value => value.Id == id);
            var replacement = attachments.Attach(allocation.Asset.Kind, UyaAssetImportService.CanonicalFormatVersion,
                bytes, existing is null ? allocation.Asset.Id : existing.ParentId);
            overrides[allocation.Asset] = binding with { Replacement = replacement };
            repaired++;
        }
        if (repaired == 0) return null;
        await workspace.CommitAssetsAsync(workspace.Content with
        {
            AssetOverrides = overrides.Values.OrderBy(value => value.Source.Kind)
                .ThenBy(value => value.Source.Id.ToString(), StringComparer.Ordinal).ToArray(),
        }, attachments.Attachments, cancellationToken);
        return new("dl-assets.conversion-repaired", EditorDiagnosticSeverity.Info,
            $"Repaired model/texture conversion for {repaired} DL asset(s). Save the project and rebuild occlusion and the map.");
    }
}
