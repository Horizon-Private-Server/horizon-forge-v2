using Forge.Host.Domain;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.LevelAssets;
using RatchetPs2.Core.Textures.Pif;
using RatchetPs2.Core.Textures.Png;
using RatchetPs2.Core.Tfrags;
using RatchetPs2.Core.Wad.Models;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.UYA;

internal static class UyaCustomTfragPreviewService
{
    public static async Task<PackedFilePackage> BuildAsync(
        ForgeProjectWorkspace workspace,
        AssetId assetId,
        CancellationToken cancellationToken)
    {
        var matches = workspace.Content.Entities
            .Where(entity => entity.Asset is { Kind: AssetKind.Tfrag } asset
                && asset.Id == assetId
                && entity.Tfrag?.CustomImportId is not null)
            .Select(entity => entity.Tfrag!.CustomImportId!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (matches.Length == 0)
            throw new InvalidDataException($"Custom tfrag {assetId} is not present in the active project.");
        var imports = matches.Select(importId => workspace.Content.Tfrags!.CustomImports
                .Single(value => value.ImportId == importId))
            .ToArray();
        if (imports.Length != 1)
            throw new InvalidDataException("Custom tfrag geometry is shared by multiple source imports.");

        var import = imports[0];
        var entities = import.Pieces.Select(id => workspace.Content.Entities
                .Single(entity => entity.EntityId == id))
            .OrderBy(entity => entity.Tfrag!.GroupIndex)
            .ToArray();
        var pieces = new List<StaticModelTfragPiece>(entities.Length);
        foreach (var entity in entities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var canonical = await workspace.ReadAttachedAssetVerifiedAsync(
                entity.Asset!, ForgeProjectWorkspace.MaxAttachedAssetBytes, cancellationToken);
            var geometry = TfragPieceCanonicalCodec.Read(canonical);
            pieces.Add(new(
                entity.Tfrag!.GroupIndex,
                new(
                    TfragPieceCanonicalCodec.ComputeContentId(canonical),
                    TfragPieceCanonicalCodec.FormatVersion,
                    canonical,
                    geometry),
                new(
                    Vector(entity.Tfrag.SourcePosition!),
                    new(0, 0, 0, 1),
                    new(1, 1, 1)),
                [])
            {
                StreamingTextureIds = geometry.TextureIds,
            });
        }

        var files = new List<PackedFile>();
        var textureUris = new Dictionary<int, string>();
        var textureSizes = new Dictionary<int, TextureSize>();
        var textureAlpha = new Dictionary<int, TextureAlphaInfo>();
        for (var index = 0; index < import.Textures.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pif = await workspace.ReadAttachedAssetVerifiedAsync(
                import.Textures[index], ForgeProjectWorkspace.MaxAttachedAssetBytes, cancellationToken);
            var texture = PifAssetExporter.Export(pif);
            var path = $"textures/texture-{index:000}.png";
            using var png = new MemoryStream(texture.PngBytes, writable: false);
            var metadata = PngTextureMetadataReader.ReadPng(png);
            textureUris.Add(index, path);
            textureSizes.Add(index, metadata.Size);
            textureAlpha.Add(index, metadata.Alpha);
            files.Add(new(path, texture.PngBytes, "image/png"));
        }

        var export = await Task.Run(() =>
        {
            var terrain = TfragTerrainReader.Read(
                StaticModelTfragConverter.BuildTerrain(GameId.UYA, pieces, cancellationToken));
            return TfragGltfExporter.Export(terrain, "model.gltf", new()
            {
                BufferFileName = "model.buffer.bin",
                GameLabel = "UYA",
                ExternalTextureUris = textureUris,
                ExternalTextureSizes = textureSizes,
                ExternalTextureAlpha = textureAlpha,
                IncludeDiagnostics = false,
                Minify = true,
                LodIndex = 0,
            });
        }, cancellationToken);
        files.Insert(0, new("model.buffer.bin", export.BinBytes, "application/octet-stream"));
        files.Insert(0, new("model.gltf", export.GltfBytes, "model/gltf+json"));
        return PackedFilePackageBuilder.Pack(files);
    }

    public static string? ResolveRevision(ForgeProjectWorkspace workspace, AssetId assetId)
    {
        var import = workspace.Content.Entities
            .Where(entity => entity.Asset is { Kind: AssetKind.Tfrag } asset
                && asset.Id == assetId)
            .Select(entity => entity.Tfrag?.CustomImportId)
            .OfType<string>()
            .Select(importId => workspace.Content.Tfrags?.CustomImports
                .SingleOrDefault(value => value.ImportId == importId))
            .FirstOrDefault(value => value is not null);
        return import is null
            ? null
            : $"{import.ImportId}:{import.Source.Id}:"
                + string.Join(':', import.Textures.Select(value => value.Id.ToString()));
    }

    private static TfragPieceVector3 Vector(ProjectVector3 value) => new(value.X, value.Y, value.Z);
}
