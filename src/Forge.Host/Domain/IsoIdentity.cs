namespace Forge.Host.Domain;

public sealed record IsoIdentity(
    bool IsSupported,
    string Game,
    string Region,
    string Revision,
    string Serial,
    long Size,
    string Fingerprint,
    string Diagnostic);
