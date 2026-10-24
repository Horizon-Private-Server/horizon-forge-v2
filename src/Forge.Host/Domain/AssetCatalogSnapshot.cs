using System.Collections.Concurrent;

namespace Forge.Host.Domain;

internal sealed class AssetCatalogSnapshot
{
    public static readonly AssetCatalogSnapshot Empty = new(new Dictionary<string, AssetCatalogEntry>(StringComparer.Ordinal));
    private readonly AssetCatalogEntry[] _byId;
    private readonly IReadOnlyDictionary<AssetKind, AssetCatalogEntry[]> _byKind;
    private readonly ConcurrentDictionary<AssetKind, Lazy<AssetCatalogEntry[]>> _byClass = new();

    public IReadOnlyDictionary<string, AssetCatalogEntry> Entries { get; }
    public string Revision { get; }

    public AssetCatalogSnapshot(IReadOnlyDictionary<string, AssetCatalogEntry> entries)
    {
        Entries = entries;
        _byId = entries.Values.OrderBy(entry => entry.Id.ToString(), StringComparer.Ordinal).ToArray();
        _byKind = _byId.GroupBy(entry => entry.Kind).ToDictionary(group => group.Key, group => group.ToArray());
        Revision = AssetCatalogPaging.ComputeRevision(_byId);
    }

    public IReadOnlyList<AssetCatalogEntry> Ordered(AssetKind? kind = null, AssetCatalogOrder order = AssetCatalogOrder.AssetId)
    {
        if (kind is null) return _byId;
        var entries = _byKind.GetValueOrDefault(kind.Value) ?? [];
        if (order == AssetCatalogOrder.AssetId) return entries;
        return _byClass.GetOrAdd(kind.Value, _ => new Lazy<AssetCatalogEntry[]>(() =>
            entries.OrderBy(entry => AssetCatalogPaging.ClassIds(entry).FirstOrDefault(int.MaxValue))
                .ThenBy(entry => entry.Id.ToString(), StringComparer.Ordinal).ToArray())).Value;
    }
}
