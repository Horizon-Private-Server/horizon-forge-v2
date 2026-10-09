using Forge.Host.Games.UYA;
using Forge.Host.Domain;
using Forge.Host.Bridge;
using RatchetPs2.Core.Games;

internal static class AssetCatalogTests
{
    public static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"forge-catalog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await VerifyCatalogAsync(Path.Combine(directory, "catalog"));
            await VerifyExplorerBridgeAsync(Path.Combine(directory, "explorer"));
            await VerifyTransactionalFailureAsync(Path.Combine(directory, "failure"));
            await VerifyGarbageCollectionAsync(Path.Combine(directory, "garbage"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task VerifyExplorerBridgeAsync(string root)
    {
        var store = await AssetCatalogStore.OpenAsync(root);
        var entry = await store.PutAsync(
            AssetKind.Tie,
            2,
            "explorer tie"u8.ToArray(),
            Metadata("level03", 7, ["tie:291", "tie:0x0123"], ["vanilla"]));
        var request = new AssetExplorerRequestPayload(
            root, AssetExplorerCategoryPayload.Ties, "0x0123", "UYA", "level03", "NTSC-U", "1.00",
            ["vanilla"], null, 64, "UYA", "NTSC-U", "1.00", 0);
        var page = await QueryExplorerAsync(request);
        Equal(entry.Id.ToString(), page.Items.Single().AssetId, "explorer bridge asset");
        Equal(true, page.Items[0].CanPlace, "explorer bridge tie placement support");
        Equal(null, page.Items[0].PlacementDisabledReason, "explorer bridge tie placement reason");
        Equal(true, page.Items[0].ClassIds.SequenceEqual([291u]), "explorer bridge normalized class identity");

        var texture = await store.PutAsync(
            AssetKind.Texture,
            1,
            "explorer texture"u8.ToArray(),
            Metadata("level03", 7, ["Crate material"], ["vanilla"],
                new(AssetKind.Tie, 291, "material", 0, true)));
        var texturePage = await QueryExplorerAsync(request with
        {
            Category = AssetExplorerCategoryPayload.Textures,
            Search = "0x0123",
        });
        var textureItem = texturePage.Items.Single();
        Equal(texture.Id.ToString(), textureItem.AssetId, "explorer texture asset");
        Equal(false, textureItem.CanPlace, "explorer texture placement support");
        Equal("Textures are preview-only.", textureItem.PlacementDisabledReason, "explorer texture placement reason");
        Equal(new AssetExplorerTextureUsePayload("tie", 291, "material", 0, true),
            textureItem.Sources.Single().TextureUse, "explorer texture provenance");

        var skyBytes = BuildUyaSkyboxFixture(2);
        var sky = await store.PutAsync(
            AssetKind.Sky,
            0,
            skyBytes,
            Metadata("level03", 0,
                ["base:sky:sky", .. SkyShellCatalogService.Aliases(skyBytes, GameId.UYA)], ["vanilla", "base-layer"]));
        var skyRequest = request with
        {
            Category = AssetExplorerCategoryPayload.SkyShells,
            Search = null,
            Limit = 1,
        };
        var firstSkyPage = await QueryExplorerAsync(skyRequest);
        Equal(0u, firstSkyPage.Items.Single().ShellIndex, "explorer first sky shell index");
        Equal(true, firstSkyPage.Items.Single().CanPlace, "explorer sky shell placement support");
        Equal(true, firstSkyPage.NextCursor is not null, "explorer sky shell cursor");
        var secondSkyPage = await QueryExplorerAsync(skyRequest with { Cursor = firstSkyPage.NextCursor });
        Equal(1u, secondSkyPage.Items.Single().ShellIndex, "explorer second sky shell index");
        var searchedSkyPage = await QueryExplorerAsync(skyRequest with { Search = "shell 01", Limit = 64 });
        Equal(1u, searchedSkyPage.Items.Single().ShellIndex, "explorer sky shell search");
        var fullSkyPage = await QueryExplorerAsync(skyRequest with { CurrentSkyShellCount = 8, Limit = 64 });
        Equal(true, fullSkyPage.Items.All(item => !item.CanPlace
            && item.PlacementDisabledReason == "UYA skyboxes support at most 8 shells."),
            "explorer sky shell limit reason");
        File.Delete(store.ResolveBlobPath(sky.Id)!);
        var missingSkyPage = await QueryExplorerAsync(skyRequest with { Limit = 64 });
        Equal(true, missingSkyPage.Items.Count == 2
            && missingSkyPage.Items.All(item => item.PreviewState == "missingBlob"),
            "explorer retained sky shell index");

        await store.UpdateMetadataAsync(
            entry.Id,
            Metadata("level03", 7, ["tie:292"], ["vanilla"]));
        await ExpectAsync<InvalidDataException>(() => QueryExplorerAsync(
            skyRequest with { Cursor = firstSkyPage.NextCursor }));
        var lowerClass = await store.PutAsync(
            AssetKind.Tie,
            2,
            "lower explorer tie"u8.ToArray(),
            Metadata("level03", 8, ["tie:2"], ["vanilla"]));
        var lowerClassVariant = await store.PutAsync(
            AssetKind.Tie,
            2,
            "lower explorer tie variant"u8.ToArray(),
            Metadata("level03", 10, ["tie:2"], ["vanilla"]));
        var higherClass = await store.PutAsync(
            AssetKind.Tie,
            2,
            "higher explorer tie"u8.ToArray(),
            Metadata("level03", 9, ["tie:400"], ["vanilla"]));
        page = await QueryExplorerAsync(request with { Search = null });
        var lowerIds = new[] { lowerClass.Id.ToString(), lowerClassVariant.Id.ToString() }
            .Order(StringComparer.Ordinal).ToArray();
        Equal(true, page.Items.Select(item => item.AssetId).SequenceEqual(
            [.. lowerIds, entry.Id.ToString(), higherClass.Id.ToString()]),
            "explorer bridge class order");
        var ambiguous = page.Items.Single(item => item.AssetId == entry.Id.ToString());
        Equal(true, ambiguous.CanPlace, "explorer family supplies the selected class identity");
        Equal(null, ambiguous.PlacementDisabledReason, "explorer multi-class placement reason");

        var firstPage = await QueryExplorerAsync(request with { Search = null, Limit = 2 });
        var secondPage = await QueryExplorerAsync(request with { Search = null, Limit = 2, Cursor = firstPage.NextCursor });
        Equal(true, firstPage.Items.Select(item => item.AssetId).SequenceEqual(lowerIds),
            "explorer bridge keeps class family in one page");
        Equal(entry.Id.ToString(), secondPage.Items[0].AssetId, "explorer bridge class cursor second page");

        File.Delete(store.ResolveBlobPath(entry.Id)!);
        page = await QueryExplorerAsync(request with { Search = null });
        var missing = page.Items.Single(item => item.AssetId == entry.Id.ToString());
        Equal("missingBlob", missing.PreviewState, "explorer bridge missing blob state");
        Equal("The catalog blob is missing.", missing.PlacementDisabledReason, "explorer bridge missing blob reason");
    }

    private static async Task<AssetExplorerPagePayload> QueryExplorerAsync(AssetExplorerRequestPayload request)
    {
        var frame = new BridgeFrame(
            BridgeMessageKind.Request,
            BridgeOpcode.QueryAssetExplorer,
            BridgeErrorCode.None,
            1,
            AssetExplorerPayloadCodec.EncodeRequest(request));
        var payload = await ProjectBridgeHandlers.HandleAsync(
            frame, "test", _ => ValueTask.CompletedTask, CancellationToken.None);
        return AssetExplorerPayloadCodec.DecodePage(payload);
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
        Equal(true, (await AssetCatalogBlobReader.ReadVerifiedAsync(
            first, blob, bytes.Length, CancellationToken.None)).SequenceEqual(bytes), "verified catalog blob read");
        await ExpectAsync<InvalidDataException>(() => AssetCatalogBlobReader.ReadVerifiedAsync(
            first, blob, bytes.Length - 1, CancellationToken.None));
        var corruptBlob = Path.Combine(root, "corrupt.blob");
        await File.WriteAllBytesAsync(corruptBlob, bytes.Select(value => (byte)(value ^ 0xff)).ToArray());
        await ExpectAsync<InvalidDataException>(() => AssetCatalogBlobReader.ReadVerifiedAsync(
            first, corruptBlob, bytes.Length, CancellationToken.None));
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
        var secondTie = await store.PutAsync(
            AssetKind.Tie,
            canonicalFormatVersion: 0,
            "second tie bytes"u8.ToArray(),
            Metadata("level09", 15, ["Bridge"], ["structural", "vanilla"]));

        Equal(first.Id, store.Query(new(Id: first.Id)).Single().Id, "query by ID");
        Equal(2, store.Query(new(Kind: AssetKind.Tie)).Count, "query by kind");
        Equal(3, store.Query(new(Game: "UYA")).Count, "query by game");
        Equal(2, store.Query(new(Level: "level03")).Count, "query by level");
        Equal(shrub.Id, store.Query(new(Tags: ["foliage", "vanilla"])).Single().Id, "query by tags");
        var expectedFirst = new[] { first.Id, shrub.Id, secondTie.Id }
            .OrderBy(id => id.ToString(), StringComparer.Ordinal).First();
        Equal(expectedFirst, store.Query(new(Limit: 1)).Single().Id, "deterministic bounded query");
        Expect<ArgumentOutOfRangeException>(() => store.Query(new(Limit: AssetCatalogStore.MaxQueryLimit + 1)));

        var search = store.QueryPage(new(AssetKind.Tie, Search: "EXPLOSIVE"));
        Equal(first.Id, search.Entries.Single().Id, "case-insensitive alias search");
        Equal(true, search.Facets.Levels.SequenceEqual(["level03", "level05"]), "filtered source facets");
        Equal(first.Id, store.QueryPage(new(
            AssetKind.Tie, Game: "UYA", Level: "level05", Region: "NTSC-U", Revision: "1.00",
            Tags: ["breakable", "vanilla"])).Entries.Single().Id, "combined appearance and tag filters");

        var firstPage = store.QueryPage(new(AssetKind.Tie, Limit: 1));
        Equal(1, firstPage.Entries.Count, "cursor first page count");
        Equal(true, firstPage.NextCursor is not null, "cursor first page continuation");
        var pagingReopen = await AssetCatalogStore.OpenAsync(root);
        var secondPage = pagingReopen.QueryPage(new(AssetKind.Tie, Cursor: firstPage.NextCursor, Limit: 1));
        Equal(1, secondPage.Entries.Count, "cursor second page count");
        Equal(false, firstPage.Entries[0].Id == secondPage.Entries[0].Id, "cursor has no duplicate");
        Equal(null, secondPage.NextCursor, "cursor final page");
        Expect<ArgumentException>(() => store.QueryPage(new(AssetKind.Tie, Cursor: "invalid")));
        Expect<ArgumentException>(() => store.QueryPage(new(AssetKind.Tie, Search: "different", Cursor: firstPage.NextCursor)));
        Expect<ArgumentOutOfRangeException>(() => store.QueryPage(new(
            AssetKind.Tie, Limit: AssetCatalogStore.MaxPageLimit + 1)));

        await store.PutAsync(
            AssetKind.Tie,
            canonicalFormatVersion: 0,
            "third tie bytes"u8.ToArray(),
            Metadata("level10", 16, ["Tower"], ["structural", "vanilla"]));
        Expect<InvalidDataException>(() => store.QueryPage(new(AssetKind.Tie, Cursor: firstPage.NextCursor, Limit: 1)));
        var cancelled = new CancellationToken(canceled: true);
        Expect<OperationCanceledException>(() => store.QueryPage(new(AssetKind.Tie), cancelled));

        var reopened = await AssetCatalogStore.OpenAsync(root);
        Equal(4, reopened.Query(new()).Count, "catalog reopen");
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
        IReadOnlyCollection<string> tags,
        AssetTextureUse? textureUse = null) => new(
        "uya-importer-v0",
        new("UYA", "NTSC-U", "1.00", level, $"{level}.wad", sourceIndex, UyaIsoService.SupportedMd5, textureUse),
        aliases,
        tags);

    private static byte[] BuildUyaSkyboxFixture(int shellCount)
    {
        var bytes = new byte[0x30 + (shellCount * 0x10)];
        using var stream = new MemoryStream(bytes, writable: true);
        using var writer = new BinaryWriter(stream);
        stream.Position = 6;
        writer.Write(checked((short)shellCount));
        stream.Position = 0x20;
        for (var index = 0; index < shellCount; index++) writer.Write(checked((uint)(0x30 + (index * 0x10))));
        return bytes;
    }

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
