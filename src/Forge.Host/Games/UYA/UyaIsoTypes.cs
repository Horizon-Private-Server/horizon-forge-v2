namespace Forge.Host.Games.UYA;

public sealed record UyaIsoIdentity(
    bool IsSupported,
    string Game,
    string Region,
    string Revision,
    string Serial,
    long Size,
    string Fingerprint,
    string Diagnostic);
