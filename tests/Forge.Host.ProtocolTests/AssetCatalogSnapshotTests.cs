using System.Diagnostics;
using System.Text.Json;
using Forge.Host.Domain;

internal static class AssetCatalogSnapshotTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-catalog-snapshots-{Guid.NewGuid():N}");
        try
        {
            await VerifyContentIdentityAsync(Path.Combine(root, "identity"));
            await VerifyWritersAsync(Path.Combine(root, "writers"));
            await VerifyOrderingAsync(Path.Combine(root, "ordering"));
            await VerifyBatchFailureAsync(Path.Combine(root, "batch-failure"));
            await VerifySourceIdentityAsync(Path.Combine(root, "source-identity"));
            await VerifyOversizedCommitAsync(Path.Combine(root, "oversized"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static AssetImportMetadata Metadata(string alias) => new(
        "snapshot-tests", new("UYA", "NTSC-U", "1.00", "level03", "synthetic", 0, new string('a', 32)),
        [alias], ["vanilla"]);

    private static async Task VerifyContentIdentityAsync(string root)
    {
        var store = await AssetCatalogStore.OpenAsync(root);
        var metadata = Metadata("First");
        var entry = await store.PutAsync(AssetKind.Tie, 1, "snapshot-one"u8.ToArray(), metadata);
        var original = await File.ReadAllTextAsync(store.CatalogPath);
        var modified = File.GetLastWriteTimeUtc(store.CatalogPath);
        var same = await AssetCatalogStore.OpenReadOnlyAsync(root);
        Check(ReferenceEquals(entry, same.Query(new(Id: entry.Id)).Single()), "Unchanged catalog was hydrated again.");

        await store.PutAsync(AssetKind.Tie, 1, "snapshot-one"u8.ToArray(), metadata);
        await store.UpdateMetadataAsync(entry.Id, metadata);
        Check(modified == File.GetLastWriteTimeUtc(store.CatalogPath), "Idempotent metadata rewrote the catalog.");

        var replacement = store.CatalogPath + ".replacement";
        await File.WriteAllTextAsync(replacement, original.Replace("First", "Other", StringComparison.Ordinal));
        File.SetLastWriteTimeUtc(replacement, modified);
        File.Move(replacement, store.CatalogPath, overwrite: true);
        var changed = await AssetCatalogStore.OpenReadOnlyAsync(root);
        Check(changed.Query(new(Id: entry.Id)).Single().Aliases.Single() == "Other", "Same-size/timestamp replacement served stale data.");
        Check(same.Query(new(Id: entry.Id)).Single().Aliases.Single() == "First", "A committed snapshot changed under an existing reader.");
        await File.WriteAllTextAsync(store.CatalogPath, original.Replace("First", "Third", StringComparison.Ordinal));
        File.SetLastWriteTimeUtc(store.CatalogPath, modified);
        Check((await AssetCatalogStore.OpenReadOnlyAsync(root)).Query(new(Id: entry.Id)).Single().Aliases.Single() == "Third",
            "Same-size/timestamp in-place edit served stale data.");

        await File.WriteAllTextAsync(store.CatalogPath, "{invalid}");
        await ExpectAsync<InvalidDataException>(() => AssetCatalogStore.OpenReadOnlyAsync(root));
        File.Delete(store.CatalogPath);
        Check((await AssetCatalogStore.OpenReadOnlyAsync(root)).Query(new()).Count == 0, "Removed catalog remained cached.");
        await using (var tooLarge = new FileStream(store.CatalogPath, FileMode.Create, FileAccess.Write))
            tooLarge.SetLength(AssetCatalogSnapshots.MaximumCatalogBytes + 1);
        await ExpectAsync<InvalidDataException>(() => AssetCatalogStore.OpenReadOnlyAsync(root));

        await File.WriteAllTextAsync(store.CatalogPath, original.Replace("First", "Fresh", StringComparison.Ordinal));
        var readers = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => AssetCatalogStore.OpenReadOnlyAsync(root)));
        Check(readers.Select(reader => reader.Query(new()).Single()).All(value => ReferenceEquals(value, readers[0].Query(new()).Single())),
            "Concurrent readers did not share hydrated content.");
    }

    private static async Task VerifyWritersAsync(string root)
    {
        var first = await AssetCatalogStore.OpenAsync(root);
        var second = await AssetCatalogStore.OpenAsync(root);
        var kept = await first.PutAsync(AssetKind.Tie, 1, "keep"u8.ToArray(), Metadata("tie:1"));
        var another = await second.PutAsync(AssetKind.Tie, 1, "other"u8.ToArray(), Metadata("tie:2"));
        var current = await AssetCatalogStore.OpenAsync(root);
        Check(current.Query(new()).Count == 2, "A stale store overwrote a newer commit.");

        await Task.WhenAll(Enumerable.Range(0, 8).Select(async index =>
            await (index % 2 == 0 ? first : second).PutAsync(AssetKind.Tie, 1,
                System.Text.Encoding.UTF8.GetBytes($"concurrent-{index}"), Metadata($"tie:{index + 10}"))));
        current = await AssetCatalogStore.OpenAsync(root);
        Check(current.Query(new()).Count == 10, "Concurrent stores lost committed rows.");
        var preview = current.PreviewGarbageCollection([kept.Id]);
        await first.UpdateMetadataAsync(another.Id, Metadata("Updated"));
        await ExpectAsync<IOException>(() => current.CollectGarbageAsync([kept.Id], preview.ConfirmationToken));
        Check(File.Exists(current.ResolveBlobPath(another.Id)), "Stale collection removed a committed blob.");

        var partial = Path.Combine(root, "active.partial");
        await File.WriteAllTextAsync(partial, "live writer");
        Directory.CreateDirectory(Path.Combine(root, "imports"));
        var importPartial = Path.Combine(root, "imports", "checkpoint.partial");
        await File.WriteAllTextAsync(importPartial, "another subsystem's live checkpoint");
        await using (var lease = new FileStream(Path.Combine(root, ".catalog-v0.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await ExpectAsync<OperationCanceledException>(() => AssetCatalogStore.OpenAsync(root, cancel.Token));
            Check(File.Exists(partial), "Opening the catalog deleted an active writer's partial.");
            Check((await AssetCatalogStore.OpenReadOnlyAsync(root)).Query(new()).Count == 10, "Reader blocked behind an active writer.");
        }
        await AssetCatalogStore.OpenAsync(root);
        Check(!File.Exists(partial), "Stale partial cleanup stopped working.");
        Check(File.Exists(importPartial), "Catalog cleanup deleted another subsystem's checkpoint.");
        await Task.WhenAll(RunWorkerAsync(root, "left"), RunWorkerAsync(root, "right"));
        Check((await AssetCatalogStore.OpenReadOnlyAsync(root)).Query(new()).Count == 20, "Cross-process writers lost committed rows.");
    }

    public static async Task WriteWorkerAsync(string root, string prefix)
    {
        var store = await AssetCatalogStore.OpenAsync(root);
        for (var index = 0; index < 5; index++)
            await store.PutAsync(AssetKind.Tie, 1, System.Text.Encoding.UTF8.GetBytes($"{prefix}-{index}"), Metadata($"tie:{index + 30}"));
    }

    private static async Task RunWorkerAsync(string root, string prefix)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardError = true };
        foreach (var argument in new[] { typeof(AssetCatalogSnapshotTests).Assembly.Location, "--catalog-snapshot-worker", root, prefix })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new Exception("Could not start catalog test worker.");
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync(timeout.Token);
            Check(process.ExitCode == 0, "Catalog worker failed: " + await errors);
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }

    private static async Task VerifyOversizedCommitAsync(string root)
    {
        var store = await AssetCatalogStore.OpenAsync(root);
        var kept = await store.PutAsync(AssetKind.Tie, 1, "protected"u8.ToArray(), Metadata("keep"));
        var original = await File.ReadAllBytesAsync(store.CatalogPath);
        var aliases = Enumerable.Range(0, 1_024).Select(index => $"{index:D4}" + new string('x', 4_086)).ToArray();
        var batch = Enumerable.Range(0, 17).Select(index => new AssetCatalogPut(
            AssetKind.Tie, 1, System.Text.Encoding.UTF8.GetBytes($"large-{index}"), Metadata("large") with { Aliases = aliases })).ToArray();
        await ExpectAsync<InvalidDataException>(() => store.PutManyAsync(batch));
        var after = await File.ReadAllBytesAsync(store.CatalogPath);
        Check(original.SequenceEqual(after), "Oversized commit replaced the last valid catalog.");
        Check(store.Query(new()).Single().Id == kept.Id, "Failed commit changed the live snapshot.");
        Check((await AssetCatalogStore.OpenReadOnlyAsync(root)).Query(new()).Single().Id == kept.Id, "Failed commit poisoned snapshot reuse.");
        Check(!Directory.EnumerateFiles(root, "*.partial", SearchOption.AllDirectories).Any(), "Failed commit leaked a partial catalog.");
    }

    private static async Task VerifyOrderingAsync(string root)
    {
        var store = await AssetCatalogStore.OpenAsync(root);
        var puts = Enumerable.Range(0, 24).Reverse().Select(index => new AssetCatalogPut(
            index % 5 == 0 ? AssetKind.Shrub : AssetKind.Tie, 1,
            System.Text.Encoding.UTF8.GetBytes($"ordered-{index}"), Metadata($"tie:{index % 7}"))).ToArray();
        var entries = await store.PutManyAsync(puts);
        var expected = entries.Where(entry => entry.Kind == AssetKind.Tie)
            .OrderBy(entry => AssetCatalogPaging.ClassIds(entry).FirstOrDefault(int.MaxValue))
            .ThenBy(entry => entry.Id.ToString(), StringComparer.Ordinal).Select(entry => entry.Id).ToArray();
        var seen = new List<AssetId>();
        string? cursor = null;
        do
        {
            var page = store.QueryPage(new(AssetKind.Tie, Cursor: cursor, Limit: 5, Order: AssetCatalogOrder.ClassId));
            seen.AddRange(page.Entries.Select(entry => entry.Id));
            Check(page.Facets.Levels.SequenceEqual(["level03"]), "Paging changed facet semantics.");
            cursor = page.NextCursor;
        } while (cursor is not null);
        Check(seen.SequenceEqual(expected), "Indexed pages changed family order or lost/duplicated variants.");
        Check(store.Query(new(Kind: AssetKind.Tie)).Select(entry => entry.Id)
            .SequenceEqual(entries.Where(entry => entry.Kind == AssetKind.Tie).OrderBy(entry => entry.Id.ToString(), StringComparer.Ordinal).Select(entry => entry.Id)),
            "Direct queries changed stable ID ordering.");
    }

    private static async Task VerifyBatchFailureAsync(string root)
    {
        var store = await AssetCatalogStore.OpenAsync(root);
        var kept = await store.PutAsync(AssetKind.Tie, 1, "keep"u8.ToArray(), Metadata("keep"));
        var original = await File.ReadAllBytesAsync(store.CatalogPath);
        var puts = Enumerable.Range(0, 12).Select(index => new AssetCatalogPut(
            AssetKind.Texture, 1, Enumerable.Repeat((byte)index, 256 * 1024 + index).ToArray(),
            Metadata($"batch-{index}"))).ToList();
        var broken = puts[4];
        var brokenId = AssetId.Compute(broken.Kind, broken.CanonicalFormatVersion, broken.CanonicalBytes.Span);
        var brokenPath = Path.Combine(store.BlobRootPath, brokenId.ToString()[..2], $"{brokenId}.blob");
        Directory.CreateDirectory(Path.GetDirectoryName(brokenPath)!);
        await File.WriteAllBytesAsync(brokenPath, "corrupt orphan"u8.ToArray());
        puts.Add(puts[0] with
        {
            Metadata = Metadata("duplicate") with { Source = Metadata("duplicate").Source with { SourceIndex = 99 } },
        });
        await ExpectAsync<InvalidDataException>(() => store.PutManyAsync(puts));
        var after = await File.ReadAllBytesAsync(store.CatalogPath);
        Check(original.SequenceEqual(after),
            "Failed blob batch replaced the catalog.");
        Check(store.Query(new()).Single().Id == kept.Id, "Failed blob batch changed the live snapshot.");
        Check(!Directory.EnumerateFiles(root, "*.partial", SearchOption.AllDirectories).Any(),
            "Failed blob batch returned before its partial writers stopped.");
        Check((await AssetCatalogStore.OpenReadOnlyAsync(root)).Query(new()).Single().Id == kept.Id,
            "Failed blob batch changed the reopened catalog.");

        await File.WriteAllBytesAsync(brokenPath, broken.CanonicalBytes.ToArray());
        var entries = await store.PutManyAsync(puts);
        Check(entries.Select(value => value.Id).SequenceEqual(puts.Select(value =>
                AssetId.Compute(value.Kind, value.CanonicalFormatVersion, value.CanonicalBytes.Span))),
            "Blob batch changed result ordering or duplicate IDs.");
        Check(entries[0].Sources.Count == 2 && entries[0].Aliases.Contains("duplicate"),
            "Duplicate blob preparation lost source appearances or aliases.");
        foreach (var entry in (await AssetCatalogStore.OpenReadOnlyAsync(root)).Query(new()))
        {
            var bytes = await File.ReadAllBytesAsync(store.ResolveBlobPath(entry.Id)!);
            Check(AssetId.Compute(entry.Kind, entry.CanonicalFormatVersion, bytes) == entry.Id,
                "Retried blob batch contains incomplete or corrupt output.");
        }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    private static async Task VerifySourceIdentityAsync(string root)
    {
        var source = new AssetSource("UYA", "NTSC-U", "1.00", "level03", "archive", 0, new string('a', 32),
            new(AssetKind.Tie, 100, "material", 0, true));
        AssetSource[] sources =
        [
            source,
            source with { TextureUse = source.TextureUse! with { } },
            source with { Game = "DL" },
            source with { Region = "PAL" },
            source with { Revision = "1.01" },
            source with { Level = "level04" },
            source with { Archive = "archive-\"材質✨\"" },
            source with { SourceIndex = 1 },
            source with { Fingerprint = new string('b', 32) },
            source with { TextureUse = null },
            source with { TextureUse = source.TextureUse! with { OwnerKind = AssetKind.Shrub } },
            source with { TextureUse = source.TextureUse! with { OwnerClassId = 101 } },
            source with { TextureUse = source.TextureUse! with { Role = "billboard" } },
            source with { TextureUse = source.TextureUse! with { Slot = 1 } },
            source with { TextureUse = source.TextureUse! with { Restorable = false } },
        ];
        var store = await AssetCatalogStore.OpenAsync(root);
        var puts = sources.Select(value => new AssetCatalogPut(
            AssetKind.Texture, 1, "shared texture"u8.ToArray(), new("identity-test", value))).ToArray();
        var results = await store.PutManyAsync(puts);
        var expected = sources.Select(value => JsonSerializer.Serialize(
            value with { MobySounds = null, SoundProgram = null })).Distinct(StringComparer.Ordinal).Count();
        Check(results[0].Sources.Count == expected, "Typed source identity differs from serialized identity.");
        var original = await File.ReadAllBytesAsync(store.CatalogPath);
        await store.PutManyAsync(puts);
        var repeated = await File.ReadAllBytesAsync(store.CatalogPath);
        Check(original.SequenceEqual(repeated), "Repeated source identities changed the catalog.");
    }

    private static async Task ExpectAsync<T>(Func<Task> operation) where T : Exception
    {
        try { await operation(); }
        catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
}
