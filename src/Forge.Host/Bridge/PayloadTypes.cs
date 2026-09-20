namespace Forge.Host.Bridge;

public sealed record HostHandshake(
    string HostVersion,
    string SdkRevision,
    IReadOnlyList<string> SupportedGames,
    IReadOnlyList<string> Capabilities);

public readonly record struct EchoRequest(string Message, uint DelayMs);
public readonly record struct BridgeProgress(uint Completed, uint Total);
public sealed record UyaIsoValidationPayload(
    bool IsSupported,
    string Game,
    string Region,
    string Revision,
    string Serial,
    ulong Size,
    string Fingerprint,
    string Diagnostic);
public readonly record struct DevelopmentIsoRequest(string SourcePath, string TargetPath, string Fingerprint, bool Overwrite);
public sealed record DevelopmentIsoPayload(string Path, ulong Size, string Fingerprint);
