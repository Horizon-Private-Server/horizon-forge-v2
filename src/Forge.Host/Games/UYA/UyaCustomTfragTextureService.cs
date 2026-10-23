using Forge.Host.Domain;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.LevelAssets;
using RatchetPs2.Core.Tfrags;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.UYA;

internal sealed record UyaCustomTfragTextureComposition(
    IReadOnlyList<TerrainTextureInput> Textures,
    IReadOnlyDictionary<(ProjectTfragSourceKind Kind, int Index),
        IReadOnlyDictionary<int, IReadOnlyDictionary<int, string>>> TextureAssetIdsByOwner);

internal static class UyaCustomTfragTextureService
{
    public static async Task<UyaCustomTfragTextureComposition> ComposeAsync(
        ForgeProjectWorkspace workspace,
        IReadOnlyDictionary<(ProjectTfragSourceKind Kind, int Index), byte[]> terrains,
        CancellationToken cancellationToken)
    {
        var state = workspace.Content.Tfrags;
        var references = (state?.CustomImports.SelectMany(value => value.Textures) ?? [])
            .Concat(state?.ImportedOwners.SelectMany(value => value.Textures.Select(texture => texture.Asset)) ?? [])
            .Distinct()
            .OrderBy(value => value.Id.ToString(), StringComparer.Ordinal)
            .ToArray();
        if (references.Length == 0)
            return new([], new Dictionary<(ProjectTfragSourceKind, int),
                IReadOnlyDictionary<int, IReadOnlyDictionary<int, string>>>());

        var textures = new List<TerrainTextureInput>(references.Length);
        foreach (var texture in references)
        {
            cancellationToken.ThrowIfCancellationRequested();
            textures.Add(new(texture.Id.ToString(), await workspace.ReadAttachedAssetVerifiedAsync(
                texture, ProjectTextureAssetSchema.MaximumCanonicalBytes, cancellationToken)));
        }

        var importsByPiece = state!.CustomImports
            .SelectMany(import => import.Pieces.Select(piece => (piece, import)))
            .ToDictionary(value => value.piece, value => value.import);
        var ownersByPiece = state.ImportedOwners
            .SelectMany(owner => owner.Pieces.Select(piece => (piece, owner)))
            .ToDictionary(value => value.piece, value => value.owner);
        var result = new Dictionary<(ProjectTfragSourceKind, int),
            IReadOnlyDictionary<int, IReadOnlyDictionary<int, string>>>();
        foreach (var source in state.Sources)
        {
            var key = (source.SourceKind, source.SourceIndex);
            if (!terrains.TryGetValue(key, out var terrainBytes)) continue;
            var entities = workspace.Content.Entities.Where(entity => entity.Tfrag is { } tfrag
                    && tfrag.SourceKind == key.SourceKind && tfrag.SourceIndex == key.SourceIndex)
                .OrderBy(entity => entity.Tfrag!.GroupIndex).ToArray();
            var nativeGroupIndex = 0;
            var remaps = new Dictionary<int, IReadOnlyDictionary<int, string>>();
            var consumedOwners = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entity in entities)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (importsByPiece.TryGetValue(entity.EntityId, out var import))
                {
                    var localToTarget = import.Textures.Select((value, index) =>
                            (Local: index, Target: value.Id.ToString()))
                        .ToDictionary(value => value.Local, value => value.Target);
                    var piece = await UyaTfragBakeService.LoadCustomPieceAsync(
                        workspace, entity, entity.Transform, cancellationToken);
                    var nativeGroupCount = StaticModelTfragConverter.GetNativeTerrainGroupCount(
                        GameId.UYA, piece, cancellationToken);
                    for (var index = 0; index < nativeGroupCount; index++)
                        remaps.Add(nativeGroupIndex++, localToTarget);
                }
                else if (ownersByPiece.TryGetValue(entity.EntityId, out var owner))
                {
                    if (!consumedOwners.Add(owner.OwnerId)) continue;
                    var localToTarget = owner.Textures.ToDictionary(
                        value => value.SourceTextureId, value => value.Asset.Id.ToString());
                    for (var index = 0; index < owner.Pieces.Count; index++)
                        remaps.Add(nativeGroupIndex++, localToTarget);
                }
                else nativeGroupIndex++;
            }
            if (TfragTerrainReader.Read(terrainBytes).TfragCount != nativeGroupIndex)
                throw new InvalidDataException(
                    "Tfrag native group allocation changed between terrain bake and texture allocation.");
            result.Add(key, remaps);
        }
        return new(textures, result);
    }
}
