using Forge.Host.Domain;

namespace Forge.Host.Games.UYA;

internal sealed record UyaHudBakeManifest(
    int SchemaVersion,
    bool Available,
    bool HeaderChanged,
    IReadOnlyList<int> ChangedBanks);

internal sealed record UyaHudBakeFingerprint(
    int SchemaVersion,
    ProjectHudState? Hud,
    IReadOnlyList<ProjectAssetOverride> Overrides,
    IReadOnlyList<UyaHudSourceFingerprint> SourceFiles);

internal sealed record UyaHudSourceFingerprint(
    string Name,
    string Checksum,
    long Size);

internal static class UyaHudBakeSchema
{
    public const int CurrentVersion = 1;
    public const string ManifestFileName = "manifest.json";
}
