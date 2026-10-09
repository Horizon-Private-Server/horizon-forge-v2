using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.Skyboxes;

namespace Forge.Host.Domain;

public sealed record SkyShellQuery(
    string? Search,
    string? Game,
    string? Level,
    string? Region,
    string? Revision,
    IReadOnlyList<string> Tags,
    string? Cursor,
    int Limit);

public sealed record SkyShellCatalogItem(AssetCatalogEntry Entry, int? ShellIndex, bool BlobAvailable);

public sealed record SkyShellCatalogPage(
    IReadOnlyList<SkyShellCatalogItem> Items,
    AssetCatalogFacets Facets,
    string? NextCursor);

public static class SkyShellCatalogService
{
    private const string AliasPrefix = "sky-shell:";

    public static async Task<SkyShellCatalogPage> QueryAsync(
        AssetCatalogStore catalog,
        SkyShellQuery query,
        GameId game,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit is < 1 or > AssetCatalogStore.MaxPageLimit)
            throw new ArgumentOutOfRangeException(nameof(query.Limit));
        var search = AssetCatalogPaging.NormalizeOptional(query.Search, nameof(query.Search));
        var normalized = query with
        {
            Search = search,
            Game = AssetCatalogPaging.NormalizeOptional(query.Game, nameof(query.Game)),
            Level = AssetCatalogPaging.NormalizeOptional(query.Level, nameof(query.Level)),
            Region = AssetCatalogPaging.NormalizeOptional(query.Region, nameof(query.Region)),
            Revision = AssetCatalogPaging.NormalizeOptional(query.Revision, nameof(query.Revision)),
            Tags = AssetCatalogPaging.NormalizeTags(query.Tags),
            Cursor = null,
        };
        var parents = await ReadSkyEntriesAsync(catalog, normalized, cancellationToken);
        var candidates = new List<SkyShellCatalogItem>();
        foreach (var entry in parents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = catalog.ResolveBlobPath(entry.Id);
            var indexes = ShellIndexes(entry).ToArray();
            if (indexes.Length == 0 && path is not null)
                indexes = await ReadShellIndexesAsync(entry, path, game, cancellationToken);
            if (indexes.Length == 0) indexes = [-1];
            foreach (var index in indexes)
            {
                var item = new SkyShellCatalogItem(entry, index >= 0 ? index : null, path is not null);
                if (search is null || SearchValues(item).Any(value => value.Contains(search, StringComparison.OrdinalIgnoreCase)))
                    candidates.Add(item);
            }
        }
        candidates.Sort((left, right) => StringComparer.Ordinal.Compare(Token(left), Token(right)));
        var signature = Signature(normalized);
        var offset = DecodeCursor(query.Cursor, catalog.Revision, signature);
        if (offset > candidates.Count) throw new ArgumentException("Catalog cursor is invalid.", nameof(query.Cursor));
        var items = candidates.Skip(offset).Take(query.Limit).ToArray();
        var nextOffset = offset + items.Length;
        return new(
            items,
            new(
                Values(candidates.SelectMany(item => item.Entry.Sources.Select(source => source.Game))),
                Values(candidates.SelectMany(item => item.Entry.Sources.Select(source => source.Level))),
                Values(candidates.SelectMany(item => item.Entry.Sources.Select(source => source.Region))),
                Values(candidates.SelectMany(item => item.Entry.Sources.Select(source => source.Revision))),
                Values(candidates.SelectMany(item => item.Entry.Tags))),
            nextOffset < candidates.Count ? EncodeCursor(catalog.Revision, signature, nextOffset) : null);
    }

    public static IReadOnlyList<string> Aliases(byte[] bytes, GameId game)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return SkyboxReader.Read(stream, game).Shells
            .Select(shell => $"{AliasPrefix}{shell.Index:D2}").ToArray();
    }

    private static async Task<List<AssetCatalogEntry>> ReadSkyEntriesAsync(
        AssetCatalogStore catalog,
        SkyShellQuery query,
        CancellationToken cancellationToken)
    {
        var result = new List<AssetCatalogEntry>();
        string? cursor = null;
        do
        {
            var page = catalog.QueryPage(new(
                AssetKind.Sky, Game: query.Game, Level: query.Level, Region: query.Region,
                Revision: query.Revision, Tags: query.Tags, Cursor: cursor,
                Limit: AssetCatalogStore.MaxPageLimit), cancellationToken);
            result.AddRange(page.Entries);
            cursor = page.NextCursor;
        } while (cursor is not null);
        return result;
    }

    private static IEnumerable<int> ShellIndexes(AssetCatalogEntry entry) => entry.Aliases
        .Where(alias => alias.StartsWith(AliasPrefix, StringComparison.Ordinal))
        .Select(alias => int.TryParse(alias.AsSpan(AliasPrefix.Length), out var index) ? index : -1)
        .Where(index => index is >= 0 and < SkyboxFormat.MaxShellCount)
        .Distinct()
        .Order();

    private static async Task<int[]> ReadShellIndexesAsync(
        AssetCatalogEntry entry,
        string path,
        GameId game,
        CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await AssetCatalogBlobReader.ReadVerifiedAsync(
                entry, path, ForgeProjectWorkspace.MaxAttachedAssetBytes, cancellationToken);
            return await Task.Run(() => Aliases(bytes, game)
                .Select(alias => int.Parse(alias.AsSpan(AliasPrefix.Length))).ToArray(), cancellationToken);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or IOException or OverflowException)
        {
            return [];
        }
    }

    private static IEnumerable<string> SearchValues(SkyShellCatalogItem item) => item.Entry.Aliases
        .Concat(item.Entry.Tags)
        .Append(item.Entry.Id.ToString())
        .Append(item.ShellIndex is { } index ? $"Sky shell {index:00}" : "Sky")
        .Concat(item.Entry.Sources.SelectMany(source => new[]
        {
            source.Game, source.Region, source.Revision, source.Level, source.Archive,
        }));

    private static string Token(SkyShellCatalogItem item) =>
        $"{item.Entry.Id}:{item.ShellIndex?.ToString("D6") ?? "missing"}";

    private static string Signature(SkyShellQuery query) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            query.Search, query.Game, query.Level, query.Region, query.Revision, query.Tags,
        }))).ToLowerInvariant();

    private static string EncodeCursor(string revision, string signature, int offset) => Convert.ToBase64String(
        Encoding.ASCII.GetBytes($"{revision}\n{signature}\n{offset}"))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static int DecodeCursor(string? cursor, string revision, string signature)
    {
        if (cursor is null) return 0;
        if (cursor.Length is 0 or > 512) throw new ArgumentException("Catalog cursor is invalid.", nameof(cursor));
        try
        {
            var base64 = cursor.Replace('-', '+').Replace('_', '/');
            var parts = Encoding.ASCII.GetString(Convert.FromBase64String(
                base64.PadRight((base64.Length + 3) / 4 * 4, '='))).Split('\n');
            if (parts.Length != 3 || parts[0].Length != AssetId.TextLength
                || parts[1].Length != AssetId.TextLength
                || !int.TryParse(parts[2], out var offset) || offset < 0) throw new FormatException();
            Convert.FromHexString(parts[0]);
            Convert.FromHexString(parts[1]);
            if (parts[0] != revision) throw new InvalidDataException("The asset catalog changed; restart the query.");
            if (parts[1] != signature) throw new ArgumentException("Catalog cursor does not match the query.", nameof(cursor));
            return offset;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (ArgumentException exception) when (exception.ParamName == nameof(cursor))
        {
            throw;
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            throw new ArgumentException("Catalog cursor is invalid.", nameof(cursor), exception);
        }
    }

    private static string[] Values(IEnumerable<string> values) => values
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
}
