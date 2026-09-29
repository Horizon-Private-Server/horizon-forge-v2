namespace Forge.Host.Domain;

public sealed record AssetTextureUse(
    AssetKind OwnerKind,
    int OwnerClassId,
    string Role,
    int Slot,
    bool Restorable);

public sealed record AssetSource(
    string Game,
    string Region,
    string Revision,
    string Level,
    string Archive,
    int SourceIndex,
    string Fingerprint,
    AssetTextureUse? TextureUse = null);

public sealed record AssetImportMetadata(
    string ImporterVersion,
    AssetSource Source,
    IReadOnlyCollection<string>? Aliases = null,
    IReadOnlyCollection<string>? Tags = null);

public sealed record AssetCatalogPut(
    AssetKind Kind,
    uint CanonicalFormatVersion,
    ReadOnlyMemory<byte> CanonicalBytes,
    AssetImportMetadata Metadata);

public sealed record AssetCatalogEntry(
    AssetId Id,
    AssetKind Kind,
    uint CanonicalFormatVersion,
    long Size,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<string> Tags,
    IReadOnlyList<AssetSource> Sources,
    DateTimeOffset ImportedAtUtc,
    string ImporterVersion);

public sealed record AssetCatalogQuery(
    AssetId? Id = null,
    AssetKind? Kind = null,
    string? Game = null,
    string? Level = null,
    IReadOnlyCollection<string>? Tags = null,
    int Limit = 100);

public enum AssetCatalogOrder
{
    AssetId,
    ClassId,
}

public sealed record AssetCatalogPageQuery(
    AssetKind Kind,
    string? Search = null,
    string? Game = null,
    string? Level = null,
    string? Region = null,
    string? Revision = null,
    IReadOnlyCollection<string>? Tags = null,
    string? Cursor = null,
    int Limit = AssetCatalogStore.DefaultPageLimit,
    AssetCatalogOrder Order = AssetCatalogOrder.AssetId);

public sealed record AssetCatalogFacets(
    IReadOnlyList<string> Games,
    IReadOnlyList<string> Levels,
    IReadOnlyList<string> Regions,
    IReadOnlyList<string> Revisions,
    IReadOnlyList<string> Tags);

public sealed record AssetCatalogPage(
    IReadOnlyList<AssetCatalogEntry> Entries,
    AssetCatalogFacets Facets,
    string? NextCursor);

public sealed record AssetGarbageCandidate(
    AssetId Id,
    AssetKind? Kind,
    long Size,
    bool Cataloged);

public sealed record AssetGarbageCollectionPreview(
    int CatalogAssetCount,
    int ProtectedAssetCount,
    IReadOnlyList<AssetGarbageCandidate> Candidates,
    string ConfirmationToken);

public sealed record AssetCatalogMaintenanceReport(
    int ProjectCount,
    int CatalogAssetCount,
    int ProtectedAssetCount,
    IReadOnlyList<AssetGarbageCandidate> Candidates,
    string ConfirmationToken,
    IReadOnlyList<string> Blockers);
