namespace Forge.Host.Domain;

public sealed record DevelopmentIsoResult(string Path, long Size, string Fingerprint);

public readonly record struct IsoProgress(long Completed, long Total);
