using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Forge.Host.Domain;

internal static class AssetCatalogPaging
{
    private const int MaxFilterLength = 4_096;
    private const int MaxCursorLength = 512;

    public static string ComputeRevision(IEnumerable<AssetCatalogEntry> entries)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var entry in entries.OrderBy(entry => entry.Id.ToString(), StringComparer.Ordinal))
        {
            hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(entry));
            hash.AppendData([0]);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static AssetCatalogPage Query(
        IEnumerable<AssetCatalogEntry> entries,
        string revision,
        AssetCatalogPageQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(query.Kind)) throw new ArgumentOutOfRangeException(nameof(query), "Unknown asset kind.");
        if (!Enum.IsDefined(query.Order)) throw new ArgumentOutOfRangeException(nameof(query), "Unknown asset order.");
        if (query.Limit is < 1 or > AssetCatalogStore.MaxPageLimit)
            throw new ArgumentOutOfRangeException(nameof(query),
                $"Page limit must be between 1 and {AssetCatalogStore.MaxPageLimit}.");

        var search = NormalizeOptional(query.Search, nameof(query.Search));
        var game = NormalizeOptional(query.Game, nameof(query.Game));
        var level = NormalizeOptional(query.Level, nameof(query.Level));
        var region = NormalizeOptional(query.Region, nameof(query.Region));
        var sourceRevision = NormalizeOptional(query.Revision, nameof(query.Revision));
        var tags = NormalizeTags(query.Tags);
        var signature = QuerySignature(query.Kind, search, game, level, region, sourceRevision, tags, query.Order);
        var after = DecodeCursor(query.Cursor, revision, signature, query.Order);
        var matches = new List<AssetCatalogEntry>();

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Kind != query.Kind || !MatchesSearch(entry, search) || !MatchesTags(entry, tags)) continue;
            if (!entry.Sources.Any(source =>
                    (game is null || source.Game == game)
                    && (level is null || source.Level == level)
                    && (region is null || source.Region == region)
                    && (sourceRevision is null || source.Revision == sourceRevision))) continue;
            matches.Add(entry);
        }

        matches.Sort((left, right) => StringComparer.Ordinal.Compare(OrderToken(left, query.Order), OrderToken(right, query.Order)));
        var filtered = after is null
            ? matches
            : matches.Where(entry => StringComparer.Ordinal.Compare(OrderToken(entry, query.Order), after) > 0).ToList();
        var page = TakePage(filtered, query.Limit, query.Order);
        var nextCursor = filtered.Count > page.Length
            ? EncodeCursor(revision, signature, OrderToken(page[^1], query.Order))
            : null;

        return new(page, Facets(matches), nextCursor);
    }

    private static AssetCatalogEntry[] TakePage(
        IReadOnlyList<AssetCatalogEntry> entries,
        int limit,
        AssetCatalogOrder order)
    {
        if (order != AssetCatalogOrder.ClassId) return entries.Take(limit).ToArray();
        var page = new List<AssetCatalogEntry>(limit);
        foreach (var group in entries.GroupBy(ClassOrderId))
        {
            var values = group.ToArray();
            if (page.Count > 0 && page.Count + values.Length > limit) break;
            page.AddRange(values.Take(limit - page.Count));
            if (page.Count == limit) break;
        }
        return page.ToArray();
    }

    private static bool MatchesSearch(AssetCatalogEntry entry, string? search)
    {
        if (search is null) return true;
        var values = entry.Aliases
            .Concat(entry.Tags)
            .Append(entry.Id.ToString())
            .Append(entry.Kind.ToString())
            .Concat(entry.Sources.SelectMany(source => new[]
            {
                source.Game,
                source.Region,
                source.Revision,
                source.Level,
                source.Archive,
                source.SourceIndex.ToString(CultureInfo.InvariantCulture),
            }));
        return values.Any(value => value.Contains(search, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesTags(AssetCatalogEntry entry, IReadOnlyList<string> tags) =>
        tags.All(tag => entry.Tags.Contains(tag, StringComparer.Ordinal));

    private static AssetCatalogFacets Facets(IReadOnlyList<AssetCatalogEntry> entries) => new(
        Values(entries.SelectMany(entry => entry.Sources.Select(source => source.Game))),
        Values(entries.SelectMany(entry => entry.Sources.Select(source => source.Level))),
        Values(entries.SelectMany(entry => entry.Sources.Select(source => source.Region))),
        Values(entries.SelectMany(entry => entry.Sources.Select(source => source.Revision))),
        Values(entries.SelectMany(entry => entry.Tags)));

    private static string[] Values(IEnumerable<string> values) =>
        values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static string? NormalizeOptional(string? value, string name)
    {
        if (value is null) return null;
        var result = value.Trim();
        if (result.Length == 0) throw new ArgumentException($"{name} cannot be empty.", name);
        if (result.Length > MaxFilterLength) throw new ArgumentException($"{name} exceeds {MaxFilterLength} characters.", name);
        return result;
    }

    private static string[] NormalizeTags(IEnumerable<string>? values)
    {
        var result = (values ?? []).Select(value => NormalizeOptional(value, "Tags")!)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (result.Length > 64) throw new ArgumentException("Tags exceed 64 items.", nameof(values));
        return result;
    }

    private static string QuerySignature(
        AssetKind kind,
        string? search,
        string? game,
        string? level,
        string? region,
        string? revision,
        IReadOnlyList<string> tags,
        AssetCatalogOrder order) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new { kind, search, game, level, region, revision, tags, order }))).ToLowerInvariant();

    private static string EncodeCursor(string revision, string signature, string assetId) =>
        Convert.ToBase64String(Encoding.ASCII.GetBytes($"{revision}\n{signature}\n{assetId}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string? DecodeCursor(
        string? cursor,
        string revision,
        string signature,
        AssetCatalogOrder order)
    {
        if (cursor is null) return null;
        if (cursor.Length is 0 or > MaxCursorLength) throw new ArgumentException("Catalog cursor is invalid.", nameof(cursor));
        try
        {
            var base64 = cursor.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight((base64.Length + 3) / 4 * 4, '=');
            var parts = Encoding.ASCII.GetString(Convert.FromBase64String(base64)).Split('\n');
            if (parts.Length != 3
                || parts[0].Length != AssetId.TextLength
                || parts[1].Length != AssetId.TextLength) throw new FormatException();
            Convert.FromHexString(parts[0]);
            Convert.FromHexString(parts[1]);
            var orderToken = NormalizeOrderToken(parts[2], order);
            if (parts[0] != revision) throw new InvalidDataException("The asset catalog changed; restart the query.");
            if (parts[1] != signature) throw new ArgumentException("Catalog cursor does not match the query.", nameof(cursor));
            return orderToken;
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

    private static string OrderToken(AssetCatalogEntry entry, AssetCatalogOrder order) => order switch
    {
        AssetCatalogOrder.AssetId => entry.Id.ToString(),
        AssetCatalogOrder.ClassId => $"{ClassIds(entry).FirstOrDefault(int.MaxValue):D10}:{entry.Id}",
        _ => throw new ArgumentOutOfRangeException(nameof(order)),
    };

    private static int ClassOrderId(AssetCatalogEntry entry) => ClassIds(entry).FirstOrDefault(int.MaxValue);

    private static string NormalizeOrderToken(string value, AssetCatalogOrder order)
    {
        if (order == AssetCatalogOrder.AssetId)
        {
            var assetId = AssetId.Parse(value).ToString();
            if (assetId != value) throw new FormatException();
            return assetId;
        }
        if (value.Length != 10 + 1 + AssetId.TextLength || value[10] != ':'
            || !int.TryParse(value.AsSpan(0, 10), NumberStyles.None, CultureInfo.InvariantCulture, out var classId)
            || classId < 0) throw new FormatException();
        var id = AssetId.Parse(value[11..]).ToString();
        var normalized = $"{classId:D10}:{id}";
        if (normalized != value) throw new FormatException();
        return normalized;
    }

    internal static int[] ClassIds(AssetCatalogEntry entry)
    {
        // ponytail: class aliases are global; replace this with typed per-game identities when M1-009 publishes them.
        var prefix = entry.Kind.ToString().ToLowerInvariant() + ":";
        var values = new List<int>();
        foreach (var alias in entry.Aliases.Where(alias => alias.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var value = alias.AsSpan(prefix.Length);
            var hexadecimal = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            if (hexadecimal) value = value[2..];
            if (int.TryParse(
                    value,
                    hexadecimal ? NumberStyles.AllowHexSpecifier : NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var classId)
                && classId >= 0) values.Add(classId);
        }
        return values.Distinct().Order().ToArray();
    }
}
