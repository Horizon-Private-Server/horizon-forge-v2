namespace Forge.Host.Domain;

public static class ProjectOcclusionSchema
{
    public const int CurrentVersion = 1;
    public const uint SourcePayloadCanonicalFormatVersion = 1;
}

public sealed record ProjectOcclusionState(
    int SchemaVersion,
    int SourceLevel,
    ProjectAssetReference SourcePayload);
