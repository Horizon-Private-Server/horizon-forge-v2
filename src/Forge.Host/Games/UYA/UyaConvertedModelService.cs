using Forge.Host.Domain;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.LevelAssets;
using RatchetPs2.Core.Textures.Palettes;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.UYA;

internal sealed record UyaConvertedModelSource(CatalogModel Model, AssetId Identity, byte[] OriginalBytes);

internal static class UyaConvertedModelService
{
    private const int MaximumCacheEntries = 1024;
    private const long MaximumCacheBytes = 128 * 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly Dictionary<(AssetId Source, AssetKind Kind, int ClassId), IReadOnlyList<byte[]>> Cache = new();
    private static readonly Queue<(AssetId Source, AssetKind Kind, int ClassId)> Order = new();
    private static long _cacheBytes;

    public static async Task<UyaConvertedModelSource> ReadAsync(AssetCatalogStore catalog, AssetCatalogEntry entry,
        int sourceClassId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!entry.Sources.Any(UyaAssetSourceCompatibility.CanConvert)
            || entry.Tags.Contains("texture:placeholder", StringComparer.Ordinal))
            throw new InvalidDataException("The catalog asset has no complete supported source.");
        var path = catalog.ResolveBlobPath(entry.Id) ?? throw new FileNotFoundException("The source model blob is missing.");
        var bytes = await AssetCatalogBlobReader.ReadVerifiedAsync(entry, path, ForgeProjectWorkspace.MaxAttachedAssetBytes, token);
        var original = CatalogModelCodec.Decode(bytes);
        var model = await UyaConvertedRc1TextureRepairService.FindUpdatedSourceAsync(catalog, entry, original, sourceClassId, token) ?? original;
        if (!entry.Sources.Any(value => value.Game == model.Game.ToString() && UyaAssetSourceCompatibility.CanConvert(value)))
            throw new InvalidDataException("The source model blob does not match a supported catalog source.");
        if (model.DefinitionBytes.Length != (entry.Kind == AssetKind.Shrub ? 0x30 : 0x20))
            throw new InvalidDataException("The source definition does not match its asset kind.");
        var identity = ReferenceEquals(model, original) ? entry.Id : AssetId.Compute(entry.Kind, entry.CanonicalFormatVersion,
            CatalogModelCodec.Encode(model.Game, model.DefinitionBytes, model.ModelBytes, model.Textures));
        return new(model, identity, bytes);
    }

    public static async Task<byte[]> ConvertAsync(UyaConvertedModelSource source, AssetKind assetKind,
        int targetClassId, CancellationToken token)
    {
        var parts = await ConvertPartsAsync(source, assetKind, targetClassId, token);
        return parts.Count == 1 ? parts[0]
            : throw new NotSupportedException("This model requires multiple Ties. Import it through Import map content.");
    }

    public static byte[] RebaseTie(byte[] canonical, int classId)
    {
        var model = UyaCanonicalAssetCodec.Decode(canonical);
        return UyaCanonicalAssetCodec.Encode(model.DefinitionBytes,
            LevelAssetModelConverter.RebaseClass(GameId.UYA, FrontendAssetKind.Tie, model.ModelBytes, classId), model.Textures);
    }

    public static async Task<IReadOnlyList<byte[]>> ConvertPartsAsync(UyaConvertedModelSource source, AssetKind assetKind,
        int targetClassId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var key = (source.Identity, assetKind, targetClassId);
        lock (Gate) if (Cache.TryGetValue(key, out var cached)) return cached;
        var kind = assetKind switch
        {
            AssetKind.Tie => FrontendAssetKind.Tie,
            AssetKind.Shrub => FrontendAssetKind.Shrub,
            AssetKind.Moby => FrontendAssetKind.Moby,
            _ => throw new NotSupportedException($"{assetKind} conversion is not supported."),
        };
        var native = await Task.Run(() =>
        {
            var model = source.Model;
            var converted = LevelAssetModelConverter.ConvertParts(model.Game, GameId.UYA, kind,
                model.ModelBytes, targetClassId, token, model.Textures);
            var textures = model.Textures.Select(texture =>
            {
                token.ThrowIfCancellationRequested();
                return texture with { PifBytes = LevelAssetModelConverter.ConvertTexture(model.Game, GameId.UYA,
                    texture.PifBytes, (TextureRole)texture.Role) };
            }).ToArray();
            return converted.Select(part => UyaCanonicalAssetCodec.Encode(model.DefinitionBytes, part, textures)).ToArray();
        }, token);
        token.ThrowIfCancellationRequested();
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
            var size = native.Sum(part => part.LongLength);
            if (size > MaximumCacheBytes) return native;
            while (_cacheBytes + size > MaximumCacheBytes || Cache.Count >= MaximumCacheEntries)
                if (Cache.Remove(Order.Dequeue(), out var evicted)) _cacheBytes -= evicted.Sum(part => part.LongLength);
            Cache.Add(key, native);
            Order.Enqueue(key);
            _cacheBytes += size;
        }
        return native;
    }
}
