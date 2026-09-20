using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Forge.Host.Domain;

public sealed class AssetCatalogStore
{
    public const int SchemaVersion = 0;
    public const int MaxQueryLimit = 1_000;
    private const long MaxCatalogBytes = 64L * 1024 * 1024;
    private const int MaxMetadataItems = 1_024;
    private const int MaxTextLength = 4_096;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        MaxDepth = 32,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly SemaphoreSlim _writes = new(1, 1);
    private readonly HashSet<string> _verifiedBlobs = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, AssetCatalogEntry> _entries;

    private AssetCatalogStore(string rootPath, IReadOnlyDictionary<string, AssetCatalogEntry> entries)
    {
        RootPath = rootPath;
        BlobRootPath = Path.Combine(rootPath, "blobs");
        CatalogPath = Path.Combine(rootPath, "catalog-v0.json");
        _entries = entries;
    }

    public string RootPath { get; }
    public string BlobRootPath { get; }
    public string CatalogPath { get; }

    public static async Task<AssetCatalogStore> OpenAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        var root = Path.GetFullPath(rootPath);
        var blobs = Path.Combine(root, "blobs");
        Directory.CreateDirectory(blobs);
        CleanupPartials(root);

        var catalogPath = Path.Combine(root, "catalog-v0.json");
        if (!File.Exists(catalogPath)) return new(root, new Dictionary<string, AssetCatalogEntry>(StringComparer.Ordinal));
        if (new FileInfo(catalogPath).Length > MaxCatalogBytes) throw new InvalidDataException("Asset catalog exceeds the size limit.");

        await using var stream = new FileStream(
            catalogPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        CatalogDocument document;
        try
        {
            document = await JsonSerializer.DeserializeAsync<CatalogDocument>(stream, JsonOptions, cancellationToken)
                ?? throw new InvalidDataException("Asset catalog is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Asset catalog contains invalid JSON.", exception);
        }

        if (document.SchemaVersion != SchemaVersion)
            throw new InvalidDataException($"Unsupported asset catalog schema {document.SchemaVersion}.");
        var entries = new Dictionary<string, AssetCatalogEntry>(StringComparer.Ordinal);
        foreach (var stored in document.Assets)
        {
            var entry = FromStored(stored);
            if (!entries.TryAdd(entry.Id.ToString(), entry))
                throw new InvalidDataException($"Asset catalog contains duplicate ID {entry.Id}.");
        }
        return new(root, entries);
    }

    public async Task<AssetCatalogEntry> PutAsync(
        AssetKind kind,
        uint canonicalFormatVersion,
        ReadOnlyMemory<byte> canonicalBytes,
        AssetImportMetadata metadata,
        CancellationToken cancellationToken = default) =>
        (await PutManyAsync([new(kind, canonicalFormatVersion, canonicalBytes, metadata)], cancellationToken))[0];

    public async Task<IReadOnlyList<AssetCatalogEntry>> PutManyAsync(
        IReadOnlyList<AssetCatalogPut> assets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assets);
        if (assets.Count == 0) return [];
        foreach (var asset in assets) ValidateMetadata(asset.Metadata);

        await _writes.WaitAsync(cancellationToken);
        try
        {
            var next = new Dictionary<string, AssetCatalogEntry>(_entries, StringComparer.Ordinal);
            var ids = new AssetId[assets.Count];
            for (var index = 0; index < assets.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var asset = assets[index];
                var id = AssetId.Compute(asset.Kind, asset.CanonicalFormatVersion, asset.CanonicalBytes.Span);
                ids[index] = id;
                await EnsureBlobAsync(
                    id, asset.Kind, asset.CanonicalFormatVersion, asset.CanonicalBytes, cancellationToken);
                next.TryGetValue(id.ToString(), out var existing);
                next[id.ToString()] = Merge(
                    existing,
                    id,
                    asset.Kind,
                    asset.CanonicalFormatVersion,
                    asset.CanonicalBytes.Length,
                    asset.Metadata);
            }
            await WriteCatalogAsync(next.Values, cancellationToken);
            _entries = next;
            return ids.Select(id => next[id.ToString()]).ToArray();
        }
        finally
        {
            _writes.Release();
        }
    }

    public async Task<AssetCatalogEntry> UpdateMetadataAsync(
        AssetId id,
        AssetImportMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        ValidateMetadata(metadata);
        await _writes.WaitAsync(cancellationToken);
        try
        {
            if (!_entries.TryGetValue(id.ToString(), out var existing))
                throw new KeyNotFoundException($"Asset {id} is not present in the catalog.");
            if (!File.Exists(BlobPath(id))) throw new FileNotFoundException($"Asset blob {id} is missing.");
            var entry = Merge(
                existing, id, existing.Kind, existing.CanonicalFormatVersion, existing.Size, metadata);
            var next = new Dictionary<string, AssetCatalogEntry>(_entries, StringComparer.Ordinal)
            {
                [id.ToString()] = entry,
            };
            await WriteCatalogAsync(next.Values, cancellationToken);
            _entries = next;
            return entry;
        }
        finally
        {
            _writes.Release();
        }
    }

    public IReadOnlyList<AssetCatalogEntry> Query(AssetCatalogQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit is < 1 or > MaxQueryLimit)
            throw new ArgumentOutOfRangeException(nameof(query), $"Query limit must be between 1 and {MaxQueryLimit}.");
        var tags = NormalizeValues(query.Tags, nameof(query.Tags));
        var id = query.Id?.ToString();
        if (id is not null && id.Length != AssetId.TextLength) throw new ArgumentException("Query Asset ID is empty.", nameof(query));

        return _entries.Values
            .Where(entry => id is null || entry.Id.ToString() == id)
            .Where(entry => query.Kind is null || entry.Kind == query.Kind)
            .Where(entry => query.Game is null || entry.Sources.Any(source => source.Game == query.Game))
            .Where(entry => query.Level is null || entry.Sources.Any(source => source.Level == query.Level))
            .Where(entry => tags.Count == 0 || tags.All(tag => entry.Tags.Contains(tag, StringComparer.Ordinal)))
            .OrderBy(entry => entry.Id.ToString(), StringComparer.Ordinal)
            .Take(query.Limit)
            .ToArray();
    }

    public string? ResolveBlobPath(AssetId id)
    {
        if (!_entries.ContainsKey(id.ToString())) return null;
        var path = BlobPath(id);
        return File.Exists(path) ? path : null;
    }

    private async Task EnsureBlobAsync(
        AssetId id,
        AssetKind kind,
        uint canonicalFormatVersion,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        var finalPath = BlobPath(id);
        if (File.Exists(finalPath))
        {
            if (_verifiedBlobs.Contains(id.ToString())) return;
            await VerifyBlobAsync(finalPath, id, kind, canonicalFormatVersion, cancellationToken);
            _verifiedBlobs.Add(id.ToString());
            return;
        }

        var directory = Path.GetDirectoryName(finalPath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{id}.{Guid.NewGuid():N}.partial");
        try
        {
            await using (var stream = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(temporary, finalPath);
                _verifiedBlobs.Add(id.ToString());
            }
            catch (IOException) when (File.Exists(finalPath))
            {
                await VerifyBlobAsync(finalPath, id, kind, canonicalFormatVersion, cancellationToken);
                _verifiedBlobs.Add(id.ToString());
            }
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static async Task VerifyBlobAsync(
        string path,
        AssetId expected,
        AssetKind kind,
        uint canonicalFormatVersion,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        Span<byte> header = stackalloc byte[AssetIdentity.HeaderLength];
        AssetIdentity.WriteHeader(header, kind, canonicalFormatVersion);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(header);
        var buffer = GC.AllocateUninitializedArray<byte>(64 * 1024);
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken)) != 0)
            hash.AppendData(buffer, 0, count);
        var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (actual != expected.ToString())
            throw new InvalidDataException($"Asset blob {expected} failed integrity verification.");
    }

    private async Task WriteCatalogAsync(IEnumerable<AssetCatalogEntry> entries, CancellationToken cancellationToken)
    {
        var document = new CatalogDocument(
            SchemaVersion,
            entries.OrderBy(entry => entry.Id.ToString(), StringComparer.Ordinal).Select(ToStored).ToList());
        var temporary = Path.Combine(RootPath, $".catalog-v0.{Guid.NewGuid():N}.partial");
        try
        {
            await using (var stream = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
                await stream.WriteAsync("\n"u8.ToArray(), cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, CatalogPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private string BlobPath(AssetId id)
    {
        var value = id.ToString();
        if (value.Length != AssetId.TextLength) throw new ArgumentException("Asset ID is empty.", nameof(id));
        return Path.Combine(BlobRootPath, value[..2], $"{value}.blob");
    }

    private static AssetCatalogEntry Merge(
        AssetCatalogEntry? existing,
        AssetId id,
        AssetKind kind,
        uint canonicalFormatVersion,
        long size,
        AssetImportMetadata metadata)
    {
        if (existing is not null && (existing.Kind != kind || existing.CanonicalFormatVersion != canonicalFormatVersion || existing.Size != size))
            throw new InvalidDataException($"Asset ID collision or inconsistent catalog metadata for {id}.");
        var aliases = MergeValues(existing?.Aliases, metadata.Aliases);
        var tags = MergeValues(existing?.Tags, metadata.Tags);
        var sources = (existing?.Sources ?? [])
            .Append(NormalizeSource(metadata.Source))
            .Distinct()
            .OrderBy(source => source.Game, StringComparer.Ordinal)
            .ThenBy(source => source.Revision, StringComparer.Ordinal)
            .ThenBy(source => source.Level, StringComparer.Ordinal)
            .ThenBy(source => source.Archive, StringComparer.Ordinal)
            .ThenBy(source => source.SourceIndex)
            .ThenBy(source => source.Fingerprint, StringComparer.Ordinal)
            .ToArray();
        if (sources.Length > MaxMetadataItems)
            throw new ArgumentException($"Asset source appearances exceed {MaxMetadataItems} items.", nameof(metadata));
        return new(
            id, kind, canonicalFormatVersion, size, aliases, tags, sources,
            existing?.ImportedAtUtc ?? DateTimeOffset.UtcNow,
            metadata.ImporterVersion.Trim());
    }

    private static IReadOnlyList<string> MergeValues(IEnumerable<string>? existing, IEnumerable<string>? added) =>
        NormalizeValues((existing ?? []).Concat(added ?? []), "metadata");

    private static IReadOnlyList<string> NormalizeValues(IEnumerable<string>? values, string name)
    {
        var result = (values ?? [])
            .Select(value => ValidateText(value, name))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (result.Length > MaxMetadataItems) throw new ArgumentException($"{name} exceeds {MaxMetadataItems} items.", name);
        return result;
    }

    private static AssetSource NormalizeSource(AssetSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.SourceIndex < 0) throw new ArgumentOutOfRangeException(nameof(source), "Source index cannot be negative.");
        return new(
            ValidateText(source.Game, nameof(source.Game)),
            ValidateText(source.Region, nameof(source.Region)),
            ValidateText(source.Revision, nameof(source.Revision)),
            ValidateText(source.Level, nameof(source.Level)),
            ValidateText(source.Archive, nameof(source.Archive)),
            source.SourceIndex,
            ValidateText(source.Fingerprint, nameof(source.Fingerprint)));
    }

    private static void ValidateMetadata(AssetImportMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ValidateText(metadata.ImporterVersion, nameof(metadata.ImporterVersion));
        NormalizeSource(metadata.Source);
        NormalizeValues(metadata.Aliases, nameof(metadata.Aliases));
        NormalizeValues(metadata.Tags, nameof(metadata.Tags));
    }

    private static string ValidateText(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        var result = value.Trim();
        if (result.Length > MaxTextLength) throw new ArgumentException($"{name} exceeds {MaxTextLength} characters.", name);
        return result;
    }

    private static StoredAsset ToStored(AssetCatalogEntry entry) => new(
        entry.Id.ToString(), entry.Kind, entry.CanonicalFormatVersion, entry.Size,
        [.. entry.Aliases], [.. entry.Tags], [.. entry.Sources], entry.ImportedAtUtc, entry.ImporterVersion);

    private static AssetCatalogEntry FromStored(StoredAsset stored)
    {
        if (!Enum.IsDefined(stored.Kind)) throw new InvalidDataException($"Unknown asset kind {stored.Kind}.");
        var id = AssetId.Parse(stored.Id);
        var source = stored.Sources.Select(NormalizeSource).ToArray();
        var aliases = NormalizeValues(stored.Aliases, nameof(stored.Aliases));
        var tags = NormalizeValues(stored.Tags, nameof(stored.Tags));
        ValidateText(stored.ImporterVersion, nameof(stored.ImporterVersion));
        if (stored.Size < 0) throw new InvalidDataException($"Asset {id} has a negative size.");
        return new(id, stored.Kind, stored.CanonicalFormatVersion, stored.Size, aliases, tags, source,
            stored.ImportedAtUtc, stored.ImporterVersion.Trim());
    }

    private static void CleanupPartials(string root)
    {
        foreach (var partial in Directory.EnumerateFiles(root, "*.partial", SearchOption.AllDirectories))
            File.Delete(partial);
    }

    private sealed record CatalogDocument(int SchemaVersion, List<StoredAsset> Assets);
    private sealed record StoredAsset(
        string Id,
        AssetKind Kind,
        uint CanonicalFormatVersion,
        long Size,
        List<string> Aliases,
        List<string> Tags,
        List<AssetSource> Sources,
        DateTimeOffset ImportedAtUtc,
        string ImporterVersion);
}
