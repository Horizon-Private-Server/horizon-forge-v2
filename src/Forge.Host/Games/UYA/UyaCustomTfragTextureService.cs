using Forge.Host.Domain;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.LevelAssets;
using RatchetPs2.Core.Tfrags;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.UYA;

internal sealed record UyaCustomTfragTextureComposition(
    IReadOnlyList<TerrainTextureInput> Textures,
    IReadOnlyDictionary<int, IReadOnlyDictionary<int, string>> TextureAssetIdsByGroup);

internal static class UyaCustomTfragTextureService
{
    public static async Task<UyaCustomTfragTextureComposition> ComposeAsync(
        ForgeProjectWorkspace workspace,
        byte[]? terrainBytes,
        CancellationToken cancellationToken)
    {
        var references = workspace.Content.Tfrags?.CustomImports
            .SelectMany(value => value.Textures)
            .Distinct()
            .OrderBy(value => value.Id.ToString(), StringComparer.Ordinal)
            .ToArray() ?? [];
        if (references.Length == 0)
            return new([], new Dictionary<int, IReadOnlyDictionary<int, string>>());
        if (terrainBytes is null)
            throw new InvalidDataException(
                "Custom tfrag textures require the current staged terrain payload; rebuild the tfrag layer.");

        var textures = new List<TerrainTextureInput>(references.Length);
        foreach (var texture in references)
        {
            cancellationToken.ThrowIfCancellationRequested();
            textures.Add(new(texture.Id.ToString(), await workspace.ReadAttachedAssetVerifiedAsync(
                texture, ProjectTextureAssetSchema.MaximumCanonicalBytes, cancellationToken)));
        }

        var importsByPiece = workspace.Content.Tfrags!.CustomImports
            .SelectMany(import => import.Pieces.Select(piece => (piece, import)))
            .ToDictionary(value => value.piece, value => value.import);
        var primaryEntities = workspace.Content.Entities.Where(entity => entity.Tfrag is
                { SourceKind: ProjectTfragSourceKind.Primary, SourceIndex: 0 })
            .OrderBy(entity => entity.Tfrag!.GroupIndex).ToArray();
        var nativeGroupIndex = primaryEntities.Count(entity => entity.Tfrag!.CustomImportId is null);
        var remaps = new Dictionary<int, IReadOnlyDictionary<int, string>>();
        foreach (var entity in primaryEntities.Where(entity => entity.Tfrag!.CustomImportId is not null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!importsByPiece.TryGetValue(entity.EntityId, out var import))
                throw new InvalidDataException($"Custom tfrag {entity.EntityId} has no owning import.");
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
        if (TfragTerrainReader.Read(terrainBytes).TfragCount != nativeGroupIndex)
            throw new InvalidDataException(
                "Custom tfrag native subdivision changed between terrain bake and texture allocation.");
        return new(textures, remaps);
    }
}
