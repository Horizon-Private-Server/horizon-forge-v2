namespace Forge.Host.Domain;

public sealed record UyaIsoIdentity(
    bool IsSupported,
    string Game,
    string Region,
    string Revision,
    string Serial,
    long Size,
    string Fingerprint,
    string Diagnostic);

public sealed record DevelopmentIsoResult(string Path, long Size, string Fingerprint);
public readonly record struct IsoProgress(long Completed, long Total);
