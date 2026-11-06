namespace Forge.Host.Domain;

internal static class AssetCatalogBlobReader
{
    public static Task<byte[]> ReadVerifiedAsync(AssetCatalogStore catalog, AssetCatalogEntry entry,
        long maxBytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (catalog.ReadTransientBlob(entry.Id) is { } memory)
        {
            if (memory.Length != entry.Size || memory.Length == 0 || memory.Length > maxBytes)
                throw new InvalidDataException($"Asset blob {entry.Id} size does not match its catalog entry.");
            if (AssetId.Compute(entry.Kind, entry.CanonicalFormatVersion, memory.Span) != entry.Id)
                throw new InvalidDataException($"Asset blob {entry.Id} failed integrity verification.");
            return Task.FromResult(memory.ToArray());
        }
        var path = catalog.ResolveBlobPath(entry.Id) ?? throw new FileNotFoundException($"Asset blob {entry.Id} is missing.");
        return ReadVerifiedAsync(entry, path, maxBytes, token);
    }

    public static Task<byte[]> ReadVerifiedAsync(
        AssetCatalogEntry entry,
        string path,
        long maxBytes,
        CancellationToken cancellationToken) =>
        ReadVerifiedAsync(
            entry.Id, entry.Kind, entry.CanonicalFormatVersion, entry.Size, path, maxBytes, cancellationToken);

    public static async Task<byte[]> ReadVerifiedAsync(
        AssetId id,
        AssetKind kind,
        uint canonicalFormatVersion,
        long expectedSize,
        string path,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (!File.Exists(path)) throw new FileNotFoundException($"Asset blob {id} is missing.", path);
        var length = new FileInfo(path).Length;
        if (length != expectedSize || length is <= 0 || length > maxBytes)
            throw new InvalidDataException($"Asset blob {id} size does not match its catalog entry.");
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (AssetId.Compute(kind, canonicalFormatVersion, bytes) != id)
            throw new InvalidDataException($"Asset blob {id} failed integrity verification.");
        return bytes;
    }
}
