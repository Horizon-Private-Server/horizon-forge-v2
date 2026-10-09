namespace Forge.Host.Games.UYA;

internal sealed record UyaFxBakeTexture(
    int Index,
    int Width,
    int Height,
    string AssetId,
    string Resource);

internal sealed record UyaFxBakeManifest(
    int SchemaVersion,
    bool Available,
    IReadOnlyList<UyaFxBakeTexture> Replacements,
    IReadOnlyList<UyaFxBakeTexture> Additions);

internal static class UyaFxBakeSchema
{
    public const int CurrentVersion = 1;
    public const string ManifestFileName = "manifest.json";
}
