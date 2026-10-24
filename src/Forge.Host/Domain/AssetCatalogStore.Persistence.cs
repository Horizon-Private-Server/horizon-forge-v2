using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Forge.Host.Domain;

public sealed partial class AssetCatalogStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        MaxDepth = 32,
        Converters = { new JsonStringEnumConverter() },
    };

    public static Task<AssetCatalogStore> OpenAsync(string rootPath, CancellationToken cancellationToken = default) =>
        OpenAsync(rootPath, prepareForWrites: true, cancellationToken);

    public static Task<AssetCatalogStore> OpenReadOnlyAsync(string rootPath, CancellationToken cancellationToken = default) =>
        OpenAsync(rootPath, prepareForWrites: false, cancellationToken);

    private static async Task<AssetCatalogStore> OpenAsync(string rootPath, bool prepareForWrites, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(rootPath);
        if (prepareForWrites)
        {
            await using var lease = await AcquireWriteLeaseAsync(root, cancellationToken);
            Directory.CreateDirectory(Path.Combine(root, "blobs"));
            // Cooperating writers hold the same lease while their partial files are live.
            var partials = Directory.EnumerateFiles(root, "*.partial", SearchOption.TopDirectoryOnly)
                .Concat(Directory.EnumerateFiles(Path.Combine(root, "blobs"), "*.partial", SearchOption.AllDirectories));
            foreach (var partial in partials) File.Delete(partial);
        }
        var snapshot = await ReadSnapshotAsync(root, cancellationToken);
        return new(root, snapshot);
    }

    private static async Task<AssetCatalogSnapshot> ReadSnapshotAsync(string root, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var catalogPath = Path.Combine(root, "catalog-v0.json");
        if (Directory.Exists(catalogPath)) return AssetCatalogSnapshot.Empty;
        FileStream stream;
        try
        {
            stream = new FileStream(catalogPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (FileNotFoundException) { return AssetCatalogSnapshot.Empty; }
        catch (DirectoryNotFoundException) { return AssetCatalogSnapshot.Empty; }
        await using (stream)
            return await AssetCatalogSnapshots.ReadAsync(stream, HydrateAsync, cancellationToken);
    }

    private static async Task<AssetCatalogSnapshot> HydrateAsync(FileStream stream, string identity, CancellationToken cancellationToken)
    {
        var length = checked((int)stream.Length);
        if (length > AssetCatalogSnapshots.MaximumCatalogBytes) throw new InvalidDataException("Asset catalog exceeds the size limit.");
        var bytes = ArrayPool<byte>.Shared.Rent(Math.Max(1, length));
        try
        {
            await stream.ReadExactlyAsync(bytes.AsMemory(0, length), cancellationToken);
            if (Convert.ToHexString(SHA256.HashData(bytes.AsSpan(0, length))) != identity)
                throw new IOException("The asset catalog changed while it was being opened; retry the operation.");
            if (await stream.ReadAsync(bytes.AsMemory(0, 1), cancellationToken) != 0)
                throw new IOException("The asset catalog changed while it was being opened; retry the operation.");
            CatalogDocument document;
            try
            {
                document = JsonSerializer.Deserialize<CatalogDocument>(bytes.AsSpan(0, length), JsonOptions)
                    ?? throw new InvalidDataException("Asset catalog is empty.");
            }
            catch (JsonException exception) { throw new InvalidDataException("Asset catalog contains invalid JSON.", exception); }
            if (document.SchemaVersion != SchemaVersion) throw new InvalidDataException($"Unsupported asset catalog schema {document.SchemaVersion}.");
            if (document.Assets is null) throw new InvalidDataException("Asset catalog has no asset list.");
            var entries = new Dictionary<string, AssetCatalogEntry>(StringComparer.Ordinal);
            foreach (var stored in document.Assets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = FromStored(stored);
                if (!entries.TryAdd(entry.Id.ToString(), entry)) throw new InvalidDataException($"Asset catalog contains duplicate ID {entry.Id}.");
            }
            return new(entries);
        }
        finally { ArrayPool<byte>.Shared.Return(bytes, clearArray: true); }
    }

    private static async Task<FileStream> AcquireWriteLeaseAsync(string root, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, ".catalog-v0.lock");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous); }
            catch (IOException exception) when ((exception.HResult & 0xFFFF) is 11 or 32 or 33)
            {
                await Task.Delay(25, cancellationToken);
            }
        }
    }

    private async Task WriteCatalogAsync(AssetCatalogSnapshot snapshot, string expectedRevision, CancellationToken cancellationToken)
    {
        var document = new CatalogDocument(SchemaVersion, snapshot.Ordered().Select(ToStored).ToList());
        var temporary = Path.Combine(RootPath, $".catalog-v0.{Guid.NewGuid():N}.partial");
        try
        {
            string identity;
            long length;
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                64 * 1024, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
                await stream.WriteAsync("\n"u8.ToArray(), cancellationToken);
                length = stream.Length;
                if (length > AssetCatalogSnapshots.MaximumCatalogBytes) throw new InvalidDataException("Asset catalog exceeds the size limit; the previous catalog was preserved.");
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
                stream.Position = 0;
                identity = await AssetCatalogSnapshots.FingerprintAsync(stream, cancellationToken);
            }
            if ((await ReadSnapshotAsync(RootPath, cancellationToken)).Revision != expectedRevision)
                throw new IOException("The asset catalog changed while it was being committed; retry the operation.");
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, CatalogPath, overwrite: true);
            AssetCatalogSnapshots.Remember(identity, snapshot, length);
        }
        finally { File.Delete(temporary); }
    }

    private sealed record CatalogDocument(int SchemaVersion, List<StoredAsset> Assets);
    private sealed record StoredAsset(
        string Id, AssetKind Kind, uint CanonicalFormatVersion, long Size,
        List<string> Aliases, List<string> Tags, List<AssetSource> Sources,
        DateTimeOffset ImportedAtUtc, string ImporterVersion);
}
