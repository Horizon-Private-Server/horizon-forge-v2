using System.Text.Json.Serialization;

namespace Forge.Updater;

internal sealed record ActivationHandoff(
    int SchemaVersion,
    string Nonce,
    string TargetRoot,
    string StageRoot,
    string ExpectedTargetPackageManifestSha256,
    string ExpectedPackageManifestSha256,
    string ExpectedVersion,
    string Executable,
    string Platform,
    int HealthTimeoutSeconds);

internal sealed record ActivationState(
    int SchemaVersion,
    string Nonce,
    string Checkpoint,
    string? Message,
    long UpdatedUnixMilliseconds);

internal sealed record PackageFile(string Path, long Size, string Sha256);

internal sealed record PackageManifest(
    int SchemaVersion,
    string Product,
    string Version,
    string Platform,
    string TopLevelDirectory,
    string Executable,
    int FileCount,
    long ExpandedSize,
    IReadOnlyList<string> ExecutablePaths,
    IReadOnlyList<PackageFile> Files);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ActivationHandoff))]
[JsonSerializable(typeof(ActivationState))]
[JsonSerializable(typeof(PackageManifest))]
internal sealed partial class UpdaterJsonContext : JsonSerializerContext;
