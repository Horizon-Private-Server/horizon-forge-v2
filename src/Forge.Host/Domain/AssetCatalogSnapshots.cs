using System.Security.Cryptography;

namespace Forge.Host.Domain;

internal static class AssetCatalogSnapshots
{
    public const long MaximumCatalogBytes = 64L * 1024 * 1024;
    private const long MaximumCachedBytes = MaximumCatalogBytes;
    private const int MaximumSnapshots = 4;
    private static readonly LinkedList<CachedSnapshot> Cache = new();
    private static readonly object CacheLock = new();
    // Bound simultaneous hydration and share the result across duplicate opens.
    private static readonly SemaphoreSlim Loading = new(1, 1);

    public static async Task<AssetCatalogSnapshot> ReadAsync(
        FileStream stream,
        Func<FileStream, string, CancellationToken, Task<AssetCatalogSnapshot>> hydrate,
        CancellationToken cancellationToken)
    {
        // shortcut: each open still reads the file; optimize this I/O if it exceeds the frozen latency budget.
        var identity = await FingerprintAsync(stream, cancellationToken);
        if (Find(identity) is { } hit) return hit;
        await Loading.WaitAsync(cancellationToken);
        try
        {
            if (Find(identity) is { } shared) return shared;
            stream.Position = 0;
            var snapshot = await hydrate(stream, identity, cancellationToken);
            Remember(identity, snapshot, stream.Length);
            return snapshot;
        }
        finally { Loading.Release(); }
    }

    public static async Task<string> FingerprintAsync(FileStream stream, CancellationToken cancellationToken)
    {
        var remaining = stream.Length;
        if (remaining > MaximumCatalogBytes) throw new InvalidDataException("Asset catalog exceeds the size limit.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (remaining > 0)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(remaining, buffer.Length)), cancellationToken);
                if (read == 0) throw new EndOfStreamException("The asset catalog changed while it was being read; retry the operation.");
                hash.AppendData(buffer.AsSpan(0, read));
                remaining -= read;
            }
            if (await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken) != 0)
                throw new IOException("The asset catalog changed while it was being read; retry the operation.");
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    public static void Remember(string identity, AssetCatalogSnapshot snapshot, long bytes)
    {
        lock (CacheLock)
        {
            for (var node = Cache.First; node is not null; node = node.Next)
                if (node.Value.Identity == identity)
                {
                    Cache.Remove(node);
                    break;
                }
            Cache.AddFirst(new CachedSnapshot(identity, snapshot, bytes));
            while (Cache.Count > MaximumSnapshots || Cache.Sum(entry => entry.Bytes) > MaximumCachedBytes)
                Cache.RemoveLast();
        }
    }

    private static AssetCatalogSnapshot? Find(string identity)
    {
        lock (CacheLock)
        {
            for (var node = Cache.First; node is not null; node = node.Next)
                if (node.Value.Identity == identity)
                {
                    Cache.Remove(node);
                    Cache.AddFirst(node);
                    return node.Value.Snapshot;
                }
            return null;
        }
    }

    private sealed record CachedSnapshot(string Identity, AssetCatalogSnapshot Snapshot, long Bytes);
}
