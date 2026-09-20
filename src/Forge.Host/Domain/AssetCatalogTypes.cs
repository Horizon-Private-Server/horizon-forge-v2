namespace Forge.Host.Domain;

public sealed record AssetSource(
    string Game,
    string Region,
    string Revision,
    string Level,
    string Archive,
    int SourceIndex,
    string Fingerprint);

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
