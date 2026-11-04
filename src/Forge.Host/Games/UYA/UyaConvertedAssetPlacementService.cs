using Forge.Host.Domain;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.LevelAssets;
using RatchetPs2.Core.Textures.Palettes;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.UYA;

internal static class UyaConvertedAssetPlacementService
{
    public static async Task<int> PrepareAsync(ForgeProjectWorkspace workspace, AssetCatalogStore catalog,
        AssetCatalogEntry entry, int sourceClassId, CancellationToken cancellationToken)
    {
        if (!entry.Sources.Any(UyaAssetSourceCompatibility.CanConvert)
            || entry.Tags.Contains("texture:placeholder", StringComparer.Ordinal))
            throw new InvalidDataException("The catalog asset has no complete supported source.");
        var reference = new ProjectAssetReference(entry.Id, entry.Kind);
        var preserveSourceClassId = entry.Kind == AssetKind.Moby || entry.Sources.Any(value => value.Game == "GC");
        var previous = workspace.Content.CustomClasses.Allocations.SingleOrDefault(value => value.Asset == reference);
        if (previous is not null)
        {
            if (previous.Provenance.Origin != ProjectCustomClassOrigin.ConvertedAsset)
                throw new InvalidDataException("The source asset already has a different class allocation.");
            if (preserveSourceClassId && previous.ClassId != sourceClassId)
                throw new InvalidDataException($"The converted {entry.Kind} is bound to class 0x{previous.ClassId:X4}; reopen the project to restore its source class.");
            return previous.ClassId;
        }
        var layer = entry.Kind.ToString().ToLowerInvariant() + "s";
        var classes = workspace.Content.CustomClasses;
        var sounds = workspace.Content.Sounds;
        if (preserveSourceClassId)
        {
            var unused = classes.Allocations.SingleOrDefault(value => value.Asset.Kind == entry.Kind
                && value.ClassId == sourceClassId && value.Provenance.Origin == ProjectCustomClassOrigin.ConvertedAsset);
            if (unused is not null)
            {
                if (workspace.Content.Entities.Any(entity => entity.Asset == unused.Asset
                    || entity.Layer == layer && entity.Source?.ClassId == sourceClassId))
                    throw new InvalidDataException($"Converted {entry.Kind} class 0x{sourceClassId:X4} already has another variant in the scene. "
                        + "Delete its instances before importing this variant; UYA supports one model per oClass.");
                // The replacement command captures the old allocation and sounds for undo.
                classes = classes with { Allocations = classes.Allocations.Where(value => value != unused).ToArray() };
                if (entry.Kind == AssetKind.Moby)
                    sounds = sounds with { MobyRemaps = sounds.MobyRemaps.Where(value =>
                        value.ClassId != sourceClassId || value.Moby != unused.Asset).ToArray() };
            }
        }
        var occupied = workspace.Content.Entities.Where(entity => entity.Layer == layer)
            .Select(entity => entity.Source?.ClassId).OfType<int>().ToHashSet();
        if (entry.Kind == AssetKind.Moby)
            occupied.UnionWith(sounds.MobyRemaps.Where(value => value.Moby != reference)
                .Select(value => value.ClassId));
        var registry = UyaCustomTieClassRegistry.AllocateConverted(
            classes, entry.Kind, entry.Id, sourceClassId, occupied,
            preserveSourceClassId: preserveSourceClassId);
        var targetClassId = registry.Allocations.Single(value => value.Asset == reference).ClassId;
        var path = catalog.ResolveBlobPath(entry.Id) ?? throw new FileNotFoundException("The source model blob is missing.");
        var bytes = await AssetCatalogBlobReader.ReadVerifiedAsync(entry.Id, entry.Kind, entry.CanonicalFormatVersion,
            entry.Size, path, ForgeProjectWorkspace.MaxAttachedAssetBytes, cancellationToken);
        var source = CatalogModelCodec.Decode(bytes);
        if (!entry.Sources.Any(value => value.Game == source.Game.ToString() && UyaAssetSourceCompatibility.CanConvert(value)))
            throw new InvalidDataException("The source model blob does not match a supported catalog source.");
        if (source.DefinitionBytes.Length != (entry.Kind == AssetKind.Shrub ? 0x30 : 0x20))
            throw new InvalidDataException("The source definition does not match its asset kind.");
        var kind = entry.Kind switch
        {
            AssetKind.Tie => FrontendAssetKind.Tie,
            AssetKind.Shrub => FrontendAssetKind.Shrub,
            AssetKind.Moby => FrontendAssetKind.Moby,
            _ => throw new NotSupportedException($"{entry.Kind} conversion is not supported."),
        };
        var nativeBytes = await Task.Run(() =>
        {
            var converted = LevelAssetModelConverter.Convert(
                source.Game, GameId.UYA, kind, source.ModelBytes, targetClassId, cancellationToken);
            var textures = source.Textures.Select(texture =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return texture with { PifBytes = LevelAssetModelConverter.ConvertTexture(
                    source.Game, GameId.UYA, texture.PifBytes, (TextureRole)texture.Role) };
            }).ToArray();
            return UyaCanonicalAssetCodec.Encode(source.DefinitionBytes, converted, textures);
        }, cancellationToken);
        var attachments = new ProjectAssetAttachmentCollector();
        attachments.Attach(entry.Kind, entry.CanonicalFormatVersion, bytes);
        var replacementId = AssetId.Compute(entry.Kind, UyaAssetImportService.CanonicalFormatVersion, nativeBytes);
        var existingReplacement = workspace.Content.Assets.SingleOrDefault(value => value.Id == replacementId);
        var parent = existingReplacement is null ? entry.Id : existingReplacement.ParentId;
        var replacement = attachments.Attach(entry.Kind, UyaAssetImportService.CanonicalFormatVersion, nativeBytes, parent);
        var overrides = workspace.Content.AssetOverrides.Where(value => value.Source != reference)
            .Append(new(ProjectAssetOverrideSchema.CurrentVersion, reference, replacement))
            .OrderBy(value => value.Source.Kind).ThenBy(value => value.Source.Id.ToString(), StringComparer.Ordinal).ToArray();
        await workspace.CommitAssetsAsync(workspace.Content with
        {
            CustomClasses = registry, AssetOverrides = overrides, Sounds = sounds,
        }, attachments.Attachments, cancellationToken);
        return targetClassId;
    }
}
