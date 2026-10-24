using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Forge.Host.Domain;

public sealed partial class AssetCatalogStore
{
    public const int SchemaVersion = 0;
    public const int MaxQueryLimit = 1_000;
    public const int DefaultPageLimit = 64;
    public const int MaxPageLimit = 128;
    private const int MaxMetadataItems = 1_024;
    private const int MaxTextLength = 4_096;
    private readonly SemaphoreSlim _writes = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _verifiedBlobs = new(StringComparer.Ordinal);
    private volatile AssetCatalogSnapshot _snapshot;

    private AssetCatalogStore(string rootPath, AssetCatalogSnapshot snapshot)
    {
        RootPath = rootPath;
        BlobRootPath = Path.Combine(rootPath, "blobs");
        CatalogPath = Path.Combine(rootPath, "catalog-v0.json");
        _snapshot = snapshot;
    }

    public string RootPath { get; }
    public string BlobRootPath { get; }
    public string CatalogPath { get; }
    internal string Revision => _snapshot.Revision;

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
        foreach (var asset in assets) ValidateMetadata(asset.Kind, asset.Metadata);

        await _writes.WaitAsync(cancellationToken);
        try
        {
            await using var lease = await AcquireWriteLeaseAsync(RootPath, cancellationToken);
            var current = await ReadSnapshotAsync(RootPath, cancellationToken);
            if (!ReferenceEquals(current, _snapshot)) _verifiedBlobs.Clear();
            var ids = new AssetId[assets.Count];
            var blobs = new Dictionary<AssetId, AssetCatalogPut>();
            for (var index = 0; index < assets.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var asset = assets[index];
                var id = AssetId.Compute(asset.Kind, asset.CanonicalFormatVersion, asset.CanonicalBytes.Span);
                ids[index] = id;
                blobs.TryAdd(id, asset);
            }
            // Overlap durable file I/O without parallel metadata mutation or extra asset copies.
            await Parallel.ForEachAsync(blobs,
                new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = cancellationToken },
                async (pair, token) => await EnsureBlobAsync(
                    pair.Key, pair.Value.Kind, pair.Value.CanonicalFormatVersion,
                    pair.Value.CanonicalBytes, token));
            var next = new Dictionary<string, AssetCatalogEntry>(current.Entries, StringComparer.Ordinal);
            var changed = false;
            for (var index = 0; index < assets.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var asset = assets[index];
                var id = ids[index];
                next.TryGetValue(id.ToString(), out var existing);
                var merged = Merge(
                    existing,
                    id,
                    asset.Kind,
                    asset.CanonicalFormatVersion,
                    asset.CanonicalBytes.Length,
                    asset.Metadata);
                next[id.ToString()] = merged;
                changed |= !ReferenceEquals(existing, merged);
            }
            var committed = changed ? new AssetCatalogSnapshot(next) : current;
            if (changed) await WriteCatalogAsync(committed, current.Revision, cancellationToken);
            _snapshot = committed;
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
        await _writes.WaitAsync(cancellationToken);
        try
        {
            await using var lease = await AcquireWriteLeaseAsync(RootPath, cancellationToken);
            var current = await ReadSnapshotAsync(RootPath, cancellationToken);
            if (!current.Entries.TryGetValue(id.ToString(), out var existing))
                throw new KeyNotFoundException($"Asset {id} is not present in the catalog.");
            ValidateMetadata(existing.Kind, metadata);
            if (!File.Exists(BlobPath(id))) throw new FileNotFoundException($"Asset blob {id} is missing.");
            var entry = Merge(
                existing, id, existing.Kind, existing.CanonicalFormatVersion, existing.Size, metadata);
            var next = new Dictionary<string, AssetCatalogEntry>(current.Entries, StringComparer.Ordinal)
            {
                [id.ToString()] = entry,
            };
            var committed = ReferenceEquals(entry, existing) ? current : new AssetCatalogSnapshot(next);
            if (!ReferenceEquals(committed, current)) await WriteCatalogAsync(committed, current.Revision, cancellationToken);
            _snapshot = committed;
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

        var snapshot = _snapshot;
        IEnumerable<AssetCatalogEntry> candidates = id is null ? snapshot.Ordered(query.Kind)
            : snapshot.Entries.TryGetValue(id, out var entry) ? [entry] : [];
        return candidates
            .Where(entry => query.Kind is null || entry.Kind == query.Kind)
            .Where(entry => query.Game is null || entry.Sources.Any(source => source.Game == query.Game))
            .Where(entry => query.Level is null || entry.Sources.Any(source => source.Level == query.Level))
            .Where(entry => tags.Count == 0 || tags.All(tag => entry.Tags.Contains(tag, StringComparer.Ordinal)))
            .Take(query.Limit)
            .ToArray();
    }

    public AssetCatalogPage QueryPage(AssetCatalogPageQuery query, CancellationToken cancellationToken = default) =>
        AssetCatalogPaging.Query(_snapshot, query, cancellationToken);

    public string? ResolveBlobPath(AssetId id)
    {
        if (!_snapshot.Entries.ContainsKey(id.ToString())) return null;
        var path = BlobPath(id);
        return File.Exists(path) ? path : null;
    }

    public AssetGarbageCollectionPreview PreviewGarbageCollection(IEnumerable<AssetId> referencedAssets)
    {
        ArgumentNullException.ThrowIfNull(referencedAssets);
        var protectedIds = referencedAssets.Select(id => id.ToString()).ToHashSet(StringComparer.Ordinal);
        if (protectedIds.Any(id => id.Length != AssetId.TextLength))
            throw new ArgumentException("Referenced Asset IDs cannot be empty.", nameof(referencedAssets));
        return BuildGarbageCollectionPreview(_snapshot, protectedIds);
    }

    public async Task<AssetGarbageCollectionPreview> CollectGarbageAsync(
        IEnumerable<AssetId> referencedAssets,
        string confirmationToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmationToken);
        var protectedIds = referencedAssets.Select(id => id.ToString()).ToHashSet(StringComparer.Ordinal);
        await _writes.WaitAsync(cancellationToken);
        try
        {
            await using var lease = await AcquireWriteLeaseAsync(RootPath, cancellationToken);
            var current = await ReadSnapshotAsync(RootPath, cancellationToken);
            var preview = BuildGarbageCollectionPreview(current, protectedIds);
            if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(preview.ConfirmationToken),
                Encoding.ASCII.GetBytes(confirmationToken)))
                throw new IOException("The asset catalog changed after the garbage-collection preview; preview it again.");

            var removedIds = preview.Candidates.Select(candidate => candidate.Id.ToString()).ToHashSet(StringComparer.Ordinal);
            var next = new Dictionary<string, AssetCatalogEntry>(
                current.Entries.Where(pair => !removedIds.Contains(pair.Key)), StringComparer.Ordinal);
            var committed = next.Count == current.Entries.Count ? current : new AssetCatalogSnapshot(next);
            if (!ReferenceEquals(committed, current)) await WriteCatalogAsync(committed, current.Revision, cancellationToken);
            _snapshot = committed;
            foreach (var candidate in preview.Candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Delete(BlobPath(candidate.Id));
                _verifiedBlobs.TryRemove(candidate.Id.ToString(), out _);
            }
            return preview;
        }
        finally
        {
            _writes.Release();
        }
    }

    private AssetGarbageCollectionPreview BuildGarbageCollectionPreview(AssetCatalogSnapshot snapshot, IReadOnlySet<string> protectedIds)
    {
        var candidates = new Dictionary<string, AssetGarbageCandidate>(StringComparer.Ordinal);
        foreach (var entry in snapshot.Entries.Values.Where(entry => !protectedIds.Contains(entry.Id.ToString())))
        {
            var path = BlobPath(entry.Id);
            candidates[entry.Id.ToString()] = new(entry.Id, entry.Kind, File.Exists(path) ? new FileInfo(path).Length : 0, true);
        }
        foreach (var path in Directory.EnumerateFiles(BlobRootPath, "*.blob", SearchOption.AllDirectories))
        {
            AssetId id;
            try { id = AssetId.Parse(Path.GetFileNameWithoutExtension(path)); }
            catch (FormatException) { continue; }
            var value = id.ToString();
            if (!protectedIds.Contains(value) && !candidates.ContainsKey(value))
                candidates[value] = new(id, null, new FileInfo(path).Length, false);
        }
        var ordered = candidates.Values.OrderBy(candidate => candidate.Id.ToString(), StringComparer.Ordinal).ToArray();
        var tokenInput = string.Join('\n', new[] { snapshot.Revision }.Concat(protectedIds.Order(StringComparer.Ordinal))
            .Concat(ordered.Select(candidate => $"{candidate.Id}:{candidate.Size}:{candidate.Cataloged}")));
        var token = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tokenInput))).ToLowerInvariant();
        return new(snapshot.Entries.Count, snapshot.Entries.Count - ordered.Count(candidate => candidate.Cataloged), ordered, token);
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
            if (_verifiedBlobs.ContainsKey(id.ToString())) return;
            await VerifyBlobAsync(finalPath, id, kind, canonicalFormatVersion, cancellationToken);
            _verifiedBlobs.TryAdd(id.ToString(), 0);
            return;
        }

        var directory = Path.GetDirectoryName(finalPath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{id}.{Guid.NewGuid():N}.partial");
        try
        {
            await using (var stream = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
                FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                // Publish only after one durable flush; syncing every partial-file write is redundant.
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(temporary, finalPath);
                _verifiedBlobs.TryAdd(id.ToString(), 0);
            }
            catch (IOException) when (File.Exists(finalPath))
            {
                await VerifyBlobAsync(finalPath, id, kind, canonicalFormatVersion, cancellationToken);
                _verifiedBlobs.TryAdd(id.ToString(), 0);
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
        var sources = MergeSources(
                existing?.Sources ?? [],
                NormalizeSource(metadata.Source, kind))
            .OrderBy(source => source.Game, StringComparer.Ordinal)
            .ThenBy(source => source.Revision, StringComparer.Ordinal)
            .ThenBy(source => source.Level, StringComparer.Ordinal)
            .ThenBy(source => source.Archive, StringComparer.Ordinal)
            .ThenBy(source => source.SourceIndex)
            .ThenBy(source => source.TextureUse?.OwnerKind)
            .ThenBy(source => source.TextureUse?.OwnerClassId)
            .ThenBy(source => source.TextureUse?.Role, StringComparer.Ordinal)
            .ThenBy(source => source.TextureUse?.Slot)
            .ThenBy(source => source.TextureUse?.Restorable)
            .ThenBy(source => source.MobySounds?.Bank.ToString(), StringComparer.Ordinal)
            .ThenBy(source => source.SoundProgram?.Bank.ToString(), StringComparer.Ordinal)
            .ThenBy(source => source.Fingerprint, StringComparer.Ordinal)
            .ToArray();
        if (sources.Length > MaxMetadataItems)
            throw new ArgumentException($"Asset source appearances exceed {MaxMetadataItems} items.", nameof(metadata));
        var merged = new AssetCatalogEntry(
            id, kind, canonicalFormatVersion, size, aliases, tags, sources,
            existing?.ImportedAtUtc ?? DateTimeOffset.UtcNow,
            metadata.ImporterVersion.Trim());
        return existing is not null
            && existing.Sources.Count == sources.Length
            && existing.Aliases.Count == aliases.Count
            && existing.Tags.Count == tags.Count
            && existing.ImporterVersion == merged.ImporterVersion
            && JsonSerializer.SerializeToUtf8Bytes(existing, JsonOptions).AsSpan()
            .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(merged, JsonOptions)) ? existing : merged;
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

    private static AssetSource NormalizeSource(AssetSource source, AssetKind kind)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.SourceIndex < 0) throw new ArgumentOutOfRangeException(nameof(source), "Source index cannot be negative.");
        var textureUse = source.TextureUse;
        if (textureUse is not null)
        {
            if (kind != AssetKind.Texture)
                throw new ArgumentException("Texture provenance is only valid for texture assets.", nameof(source));
            if (textureUse.OwnerKind is not (AssetKind.Moby or AssetKind.Tie or AssetKind.Shrub))
                throw new ArgumentOutOfRangeException(nameof(source), "Texture owner kind must be a model asset kind.");
            if (textureUse.OwnerClassId < 0)
                throw new ArgumentOutOfRangeException(nameof(source), "Texture owner class ID cannot be negative.");
            if (textureUse.Slot < 0)
                throw new ArgumentOutOfRangeException(nameof(source), "Texture slot cannot be negative.");
            textureUse = textureUse with { Role = ValidateText(textureUse.Role, nameof(textureUse.Role)) };
        }
        var mobySounds = source.MobySounds;
        if (mobySounds is not null)
        {
            if (kind != AssetKind.Moby)
                throw new ArgumentException("Moby sound provenance is only valid for Moby assets.", nameof(source));
            if (mobySounds.Bank.ToString().Length != AssetId.TextLength)
                throw new ArgumentException("Moby sound bank Asset ID is empty.", nameof(source));
            if (mobySounds.ClassId is < 0 or > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(source), "Moby sound class ID is outside the native range.");
            if (mobySounds.Slots.Count > ProjectSoundSchema.MaximumRemapSlots)
                throw new ArgumentException("Moby sound slot list exceeds the supported limit.", nameof(source));
            var slots = mobySounds.Slots.Select(slot =>
            {
                if (slot.Slot < 0 || slot.NativeProgramIndex < 0)
                    throw new ArgumentOutOfRangeException(nameof(source), "Moby sound slots and program indexes cannot be negative.");
                if (slot.Sound is { } sound && sound.ToString().Length != AssetId.TextLength)
                    throw new ArgumentException("Moby sound Asset ID is empty.", nameof(source));
                return slot with
                {
                    PreviewBlocker = string.IsNullOrWhiteSpace(slot.PreviewBlocker)
                        ? null
                        : ValidateText(slot.PreviewBlocker, nameof(slot.PreviewBlocker)),
                };
            }).OrderBy(slot => slot.Slot).ThenBy(slot => slot.NativeProgramIndex).ToArray();
            if (slots.Select(slot => slot.Slot).Distinct().Count() != slots.Length)
                throw new ArgumentException("Moby sound slots must be unique.", nameof(source));
            mobySounds = mobySounds with { Slots = slots };
        }
        var soundProgram = source.SoundProgram;
        if (soundProgram is not null)
        {
            if (kind != AssetKind.Sound)
                throw new ArgumentException("Sound-program provenance is only valid for Sound assets.", nameof(source));
            if (soundProgram.Bank.ToString().Length != AssetId.TextLength)
                throw new ArgumentException("Sound-program bank Asset ID is empty.", nameof(source));
            soundProgram = soundProgram with
            {
                ReferencedPrograms = NormalizeAssetIds(soundProgram.ReferencedPrograms, nameof(soundProgram.ReferencedPrograms)),
                Samples = NormalizeAssetIds(soundProgram.Samples, nameof(soundProgram.Samples)),
            };
        }
        return new(
            ValidateText(source.Game, nameof(source.Game)),
            ValidateText(source.Region, nameof(source.Region)),
            ValidateText(source.Revision, nameof(source.Revision)),
            ValidateText(source.Level, nameof(source.Level)),
            ValidateText(source.Archive, nameof(source.Archive)),
            source.SourceIndex,
            ValidateText(source.Fingerprint, nameof(source.Fingerprint)),
            textureUse,
            mobySounds,
            soundProgram);
    }

    private static IEnumerable<AssetSource> MergeSources(
        IReadOnlyList<AssetSource> existing,
        AssetSource added)
    {
        var identity = SourceIdentity(added);
        return existing
            .Where(source => !SourceIdentity(source).Equals(identity))
            .Append(added);
    }

    private static AssetSource SourceIdentity(AssetSource source) =>
        source with { MobySounds = null, SoundProgram = null };

    private static AssetId[] NormalizeAssetIds(IReadOnlyList<AssetId> values, string name)
    {
        if (values.Count > MaxMetadataItems)
            throw new ArgumentException($"{name} exceeds {MaxMetadataItems} items.", name);
        if (values.Any(value => value.ToString().Length != AssetId.TextLength))
            throw new ArgumentException($"{name} contains an empty Asset ID.", name);
        return values.Distinct().OrderBy(value => value.ToString(), StringComparer.Ordinal).ToArray();
    }

    private static void ValidateMetadata(AssetKind kind, AssetImportMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ValidateText(metadata.ImporterVersion, nameof(metadata.ImporterVersion));
        NormalizeSource(metadata.Source, kind);
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
        var source = stored.Sources.Select(value => NormalizeSource(value, stored.Kind)).ToArray();
        var aliases = NormalizeValues(stored.Aliases, nameof(stored.Aliases));
        var tags = NormalizeValues(stored.Tags, nameof(stored.Tags));
        ValidateText(stored.ImporterVersion, nameof(stored.ImporterVersion));
        if (stored.Size < 0) throw new InvalidDataException($"Asset {id} has a negative size.");
        return new(id, stored.Kind, stored.CanonicalFormatVersion, stored.Size, aliases, tags, source,
            stored.ImportedAtUtc, stored.ImporterVersion.Trim());
    }

}
