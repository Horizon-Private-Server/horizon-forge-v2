using System.Security.Cryptography;

namespace Forge.Host.Games.UYA;

internal sealed class UyaPreflightLayerSummaryCache
{
    private const int MaximumEntries = 4;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly LinkedList<(string ContentHash, int Count)> _summaries = new();

    public int GetOrCreate(byte[] source, Func<int> create, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Re-read and hash source bytes on every request, including timestamp-preserving edits.
        var key = Convert.ToHexString(SHA256.HashData(source));
        cancellationToken.ThrowIfCancellationRequested();
        if (TryGet(key, out var cached)) return cached;
        _gate.Wait(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryGet(key, out cached)) return cached;

            var result = create();
            cancellationToken.ThrowIfCancellationRequested();
            lock (_summaries)
            {
                if (_summaries.Count == MaximumEntries) _summaries.RemoveLast();
                // Retain only the digest and count; source bytes and decoded geometry belong to the request.
                _summaries.AddFirst((key, result));
            }
            return result;
        }
        finally { _gate.Release(); }
    }

    private bool TryGet(string key, out int count)
    {
        lock (_summaries)
        {
            for (var node = _summaries.First; node is not null; node = node.Next)
            {
                if (node.Value.ContentHash != key) continue;
                count = node.Value.Count;
                _summaries.Remove(node);
                _summaries.AddFirst(node);
                return true;
            }
        }
        count = 0;
        return false;
    }
}
