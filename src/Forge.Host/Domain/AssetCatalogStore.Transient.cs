namespace Forge.Host.Domain;

public sealed partial class AssetCatalogStore
{
    private Dictionary<AssetId, byte[]>? _transientBlobs;
    private AssetCatalogStore? _fallback;

    internal static async Task<AssetCatalogStore> CreateTransientAsync(AssetCatalogStore fallback,
        IReadOnlyList<AssetId> retainedIds, long maxBytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var retained = new Dictionary<string, AssetCatalogEntry>(StringComparer.Ordinal);
        foreach (var id in retainedIds.Distinct())
        {
            var entry = fallback.Query(new(Id: id)).SingleOrDefault();
            if (entry is null || fallback.ResolveBlobPath(id) is null) continue;
            _ = await AssetCatalogBlobReader.ReadVerifiedAsync(fallback, entry, maxBytes, token);
            retained.Add(id.ToString(), entry);
        }
        return new(null, new(retained)) { _transientBlobs = [], _fallback = fallback };
    }

    internal ReadOnlyMemory<byte>? ReadTransientBlob(AssetId id) =>
        _transientBlobs is { } blobs && blobs.TryGetValue(id, out var bytes) ? (ReadOnlyMemory<byte>?)bytes : null;

    private async Task<IReadOnlyList<AssetCatalogEntry>> PutTransientAsync(
        IReadOnlyList<AssetCatalogPut> assets, CancellationToken token)
    {
        await _writes.WaitAsync(token);
        try
        {
            var entries = new Dictionary<string, AssetCatalogEntry>(_snapshot.Entries, StringComparer.Ordinal);
            var blobs = new Dictionary<AssetId, byte[]>(_transientBlobs!);
            var ids = new List<AssetId>(assets.Count);
            foreach (var asset in assets)
            {
                token.ThrowIfCancellationRequested();
                var bytes = asset.CanonicalBytes.ToArray();
                var id = AssetId.Compute(asset.Kind, asset.CanonicalFormatVersion, bytes);
                entries.TryGetValue(id.ToString(), out var previous);
                entries[id.ToString()] = Merge(previous, id, asset.Kind, asset.CanonicalFormatVersion, bytes.Length, asset.Metadata);
                blobs.TryAdd(id, bytes);
                ids.Add(id);
            }
            token.ThrowIfCancellationRequested();
            var snapshot = new AssetCatalogSnapshot(entries);
            _transientBlobs = blobs;
            _snapshot = snapshot;
            return ids.Select(id => snapshot.Entries[id.ToString()]).ToArray();
        }
        finally { _writes.Release(); }
    }
}
