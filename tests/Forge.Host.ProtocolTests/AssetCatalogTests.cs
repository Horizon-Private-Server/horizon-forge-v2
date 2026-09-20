using Forge.Host.Domain;

internal static class AssetCatalogTests
{
    public static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"forge-catalog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await VerifyCatalogAsync(Path.Combine(directory, "catalog"));
            await VerifyTransactionalFailureAsync(Path.Combine(directory, "failure"));
            await VerifyGarbageCollectionAsync(Path.Combine(directory, "garbage"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task VerifyCatalogAsync(string root)
    {
        var store = await AssetCatalogStore.OpenAsync(root);
        var bytes = "shared tie bytes"u8.ToArray();
        var first = await store.PutAsync(
            AssetKind.Tie,
            canonicalFormatVersion: 0,
            bytes,
            Metadata("level03", 7, ["Crate"], ["interactive", "vanilla"]));
        var blob = store.ResolveBlobPath(first.Id) ?? throw new InvalidOperationException("Stored blob did not resolve.");
        var preservedWriteTime = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(blob, preservedWriteTime);

        var duplicate = await store.PutAsync(
            AssetKind.Tie,
            canonicalFormatVersion: 0,
            bytes,
            Metadata("level05", 12, ["Explosive crate"], ["interactive", "destructible"]));
        duplicate = await store.UpdateMetadataAsync(
            duplicate.Id,
            Metadata("level05", 12, ["Destructible crate"], ["breakable"]));
        Equal(first.Id, duplicate.Id, "duplicate identity");
        Equal(preservedWriteTime, File.GetLastWriteTimeUtc(blob), "metadata update did not rewrite blob");
        Equal(2, duplicate.Sources.Count, "many-to-many level sources");
        Equal(true, duplicate.Tags.SequenceEqual(["breakable", "destructible", "interactive", "vanilla"]), "sorted merged tags");
        Equal(1, Directory.EnumerateFiles(store.BlobRootPath, "*.blob", SearchOption.AllDirectories).Count(), "deduplicated blob count");

        var shrub = await store.PutAsync(
            AssetKind.Shrub,
            canonicalFormatVersion: 1,
            "shrub bytes"u8.ToArray(),
            Metadata("level03", 2, ["Fern"], ["foliage", "vanilla"]));

        Equal(first.Id, store.Query(new(Id: first.Id)).Single().Id, "query by ID");
        Equal(first.Id, store.Query(new(Kind: AssetKind.Tie)).Single().Id, "query by kind");
        Equal(2, store.Query(new(Game: "UYA")).Count, "query by game");
        Equal(2, store.Query(new(Level: "level03")).Count, "query by level");
        Equal(shrub.Id, store.Query(new(Tags: ["foliage", "vanilla"])).Single().Id, "query by tags");
        var expectedFirst = new[] { first.Id, shrub.Id }.OrderBy(id => id.ToString(), StringComparer.Ordinal).First();
        Equal(expectedFirst, store.Query(new(Limit: 1)).Single().Id, "deterministic bounded query");
        Expect<ArgumentOutOfRangeException>(() => store.Query(new(Limit: AssetCatalogStore.MaxQueryLimit + 1)));

        var reopened = await AssetCatalogStore.OpenAsync(root);
        Equal(2, reopened.Query(new()).Count, "catalog reopen");
        Equal(true, (await File.ReadAllBytesAsync(reopened.ResolveBlobPath(first.Id)!)).SequenceEqual(bytes), "reopened blob bytes");
    }

    private static async Task VerifyTransactionalFailureAsync(string root)
    {
        var stale = Path.Combine(root, "stale.partial");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(stale, "partial");
        var store = await AssetCatalogStore.OpenAsync(root);
        Equal(false, File.Exists(stale), "stale partial cleanup");

        Directory.CreateDirectory(store.CatalogPath);
        var bytes = "complete orphan"u8.ToArray();
        await ExpectFileSystemFailureAsync(() => store.PutAsync(
            AssetKind.Moby,
            canonicalFormatVersion: 0,
            bytes,
            Metadata("level09", 3, ["Vendor"], ["vanilla"])));
        Equal(0, store.Query(new()).Count, "failed insertion has no catalog row");
        Equal(0, Directory.EnumerateFiles(root, "*.partial", SearchOption.AllDirectories).Count(), "failed insertion has no partial file");
        var blobs = Directory.EnumerateFiles(store.BlobRootPath, "*.blob", SearchOption.AllDirectories).ToArray();
        Equal(1, blobs.Length, "failed catalog commit may leave one complete orphan");
        Equal(true, (await File.ReadAllBytesAsync(blobs[0])).SequenceEqual(bytes), "orphan blob is complete");

        Directory.Delete(store.CatalogPath);
        var reopened = await AssetCatalogStore.OpenAsync(root);
        Equal(0, reopened.Query(new()).Count, "failed catalog remains empty after reopen");
        var inserted = await reopened.PutAsync(
            AssetKind.Moby,
            canonicalFormatVersion: 0,
            bytes,
            Metadata("level09", 3, ["Vendor"], ["vanilla"]));
        Equal(1, reopened.Query(new(Id: inserted.Id)).Count, "complete orphan reused on retry");

        var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await ExpectAsync<OperationCanceledException>(() => reopened.PutAsync(
            AssetKind.Texture,
            canonicalFormatVersion: 0,
            "cancelled"u8.ToArray(),
            Metadata("level09", 4, ["Texture"], ["vanilla"]),
            cancellation.Token));
        Equal(1, reopened.Query(new()).Count, "cancelled insertion has no catalog row");
    }

    private static async Task VerifyGarbageCollectionAsync(string root)
    {
        var store = await AssetCatalogStore.OpenAsync(root);
        var protectedAsset = await store.PutAsync(AssetKind.Moby, 0, "keep"u8.ToArray(), Metadata("level03", 1, [], []));
        var unused = await store.PutAsync(AssetKind.Tie, 0, "remove"u8.ToArray(), Metadata("level03", 2, [], []));
        var preview = store.PreviewGarbageCollection([protectedAsset.Id]);
        Equal(1, preview.ProtectedAssetCount, "garbage preview protected count");
        Equal(unused.Id, preview.Candidates.Single().Id, "garbage preview candidate");

        var late = await store.PutAsync(AssetKind.Shrub, 0, "late"u8.ToArray(), Metadata("level03", 3, [], []));
        await ExpectAsync<IOException>(() => store.CollectGarbageAsync([protectedAsset.Id], preview.ConfirmationToken));
        Equal(true, File.Exists(store.ResolveBlobPath(late.Id)), "stale garbage preview changes nothing");

        preview = store.PreviewGarbageCollection([protectedAsset.Id]);
        await store.CollectGarbageAsync([protectedAsset.Id], preview.ConfirmationToken);
        Equal(true, File.Exists(store.ResolveBlobPath(protectedAsset.Id)), "referenced blob survives garbage collection");
        Equal(null, store.ResolveBlobPath(unused.Id), "unused blob removed");
        Equal(null, store.ResolveBlobPath(late.Id), "late unused blob removed");
        Equal(1, (await AssetCatalogStore.OpenAsync(root)).Query(new()).Count, "garbage catalog committed");
    }

    private static AssetImportMetadata Metadata(
        string level,
        int sourceIndex,
        IReadOnlyCollection<string> aliases,
        IReadOnlyCollection<string> tags) => new(
        "uya-importer-v0",
        new("UYA", "NTSC-U", "1.00", level, $"{level}.wad", sourceIndex, UyaIsoService.SupportedMd5),
        aliases,
        tags);

    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }

    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }

    private static async Task ExpectFileSystemFailureAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return; }
        throw new InvalidOperationException("Expected IOException or UnauthorizedAccessException");
    }

    private static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected {expected}, got {actual}");
    }
}
