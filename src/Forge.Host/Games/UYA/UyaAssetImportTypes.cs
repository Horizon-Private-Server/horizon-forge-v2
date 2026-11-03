namespace Forge.Host.Games.UYA;

public sealed record UyaAssetImportRequest(
    string SourceIsoPath,
    string CatalogRootPath,
    string Fingerprint,
    string Revision,
    string ImporterVersion,
    bool Force = false);
