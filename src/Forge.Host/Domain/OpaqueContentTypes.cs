namespace Forge.Host.Domain;

public sealed record OpaqueContentSource(
    string Game,
    string Region,
    string Revision,
    int Level,
    string Fingerprint);

public sealed record OpaqueSectionPlacement(
    string Container,
    int HeaderOffset,
    long Offset,
    long Length,
    int Alignment);

public sealed record OpaqueSectionRecord(
    string Id,
    string Name,
    OpaqueSectionPlacement Placement,
    string Checksum,
    long Size,
    string Blob);

public sealed record OpaqueContentManifest(
    int SchemaVersion,
    string DocumentType,
    OpaqueContentSource Source,
    IReadOnlyList<OpaqueSectionRecord> Sections);

public sealed record OpaqueContentInspection(
    OpaqueContentManifest? Manifest,
    IReadOnlyList<string> Blockers)
{
    public bool IsValid => Manifest is not null && Blockers.Count == 0;
}

internal sealed record OpaqueSectionCapture(
    string Name,
    OpaqueSectionPlacement Placement,
    byte[] Bytes);

public static class OpaqueContentSchema
{
    public const int CurrentVersion = 1;
    public const string ManifestDocumentType = "horizon-forge-opaque-content";
}
