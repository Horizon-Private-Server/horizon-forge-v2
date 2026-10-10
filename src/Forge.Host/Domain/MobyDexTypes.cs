using System.Text.Json.Serialization;

namespace Forge.Host.Domain;

public sealed record MobyDexEntry
{
    [JsonRequired]
    public int SchemaVersion { get; init; }
    [JsonRequired]
    public string Game { get; init; } = string.Empty;
    [JsonRequired]
    public int OClass { get; init; }
    [JsonRequired]
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    [JsonRequired]
    [JsonPropertyName("pvar")]
    public MobyDexPVar? PVar { get; init; }
}

public sealed record MobyDexPVar
{
    [JsonRequired]
    public int Length { get; init; }
    public string? DefaultHex { get; init; }
    public IReadOnlyList<int> Relocations { get; init; } = [];
    [JsonRequired]
    public IReadOnlyList<MobyDexField> Fields { get; init; } = [];
}

public sealed record MobyDexField
{
    [JsonRequired]
    public string Key { get; init; } = string.Empty;
    [JsonRequired]
    public string Label { get; init; } = string.Empty;
    [JsonRequired]
    public int Offset { get; init; }
    [JsonRequired]
    public int Length { get; init; }
    public string? Help { get; init; }
    public decimal? Minimum { get; init; }
    public decimal? Maximum { get; init; }
    [JsonRequired]
    public MobyDexTypeDefinition? Definition { get; init; }
}

public sealed record MobyDexTypeDefinition
{
    [JsonRequired]
    public string Kind { get; init; } = string.Empty;
    public string? Storage { get; init; }
    public IReadOnlyList<MobyDexOption>? Options { get; init; }
    public int? Count { get; init; }
    public int? Stride { get; init; }
    public MobyDexTypeDefinition? Element { get; init; }
    public IReadOnlyList<MobyDexField>? Fields { get; init; }
    public string? Domain { get; init; }
    public string? TargetKind { get; init; }
    public long? NullValue { get; init; }
}

public sealed record MobyDexOption
{
    [JsonRequired]
    public string Key { get; init; } = string.Empty;
    [JsonRequired]
    public string Label { get; init; } = string.Empty;
    [JsonRequired]
    public long Value { get; init; }
}

public static class ProjectMobyDexSchema
{
    public const int CurrentVersion = 1;
    public const int MaximumEntries = 4_096;
}

public sealed record ProjectMobyDexState(
    int SchemaVersion,
    IReadOnlyList<MobyDexEntry> Entries);

public sealed record MobyDexDataset(
    string Id,
    int Version,
    IReadOnlyList<MobyDexEntry> Entries);

public enum MobyDexEntrySource : byte
{
    BuiltIn = 1,
    Project = 2,
}

public sealed record MobyDexResolvedEntry(
    MobyDexEntry Entry,
    MobyDexEntrySource Source,
    string DatasetId,
    int DatasetVersion,
    int SchemaVersion,
    string Fingerprint);

public sealed record MobyDexDiagnostic(string Code, string Path, string Message);

public sealed class MobyDexValidationException(IReadOnlyList<MobyDexDiagnostic> diagnostics)
    : Exception(diagnostics.Count == 0
        ? "MobyDex entry is invalid."
        : $"{diagnostics[0].Code} at {diagnostics[0].Path}: {diagnostics[0].Message}")
{
    public IReadOnlyList<MobyDexDiagnostic> Diagnostics { get; } = diagnostics;
}
