namespace Forge.Host.Domain;

public enum ProjectTfragSourceKind : byte
{
    Primary = 1,
    Chunk = 2,
}

public static class ProjectTfragSchema
{
    public const int CurrentVersion = 1;
    public const uint CanonicalFormatVersion = 1;
    public const int MaximumSourceCount = 4_096;
    public const int MaximumGroupCount = 100_000;
    public const int MaximumTextureDependencies = 4_096;
}

public sealed record ProjectTfragSource(
    string SourceArchive,
    ProjectTfragSourceKind SourceKind,
    int SourceIndex,
    int GroupCount);

public sealed record ProjectTfragBounds(
    ProjectVector3 Minimum,
    ProjectVector3 Maximum,
    float Radius);

public sealed record ProjectTfragDiagnostics(
    int SourceDataLength,
    int SourceVertexCount,
    int SourceTriangleCount,
    int DecodedVertexCount,
    int DecodedTriangleCount);

public sealed record ProjectTfragPiece(
    string OccurrenceKey,
    string SourceArchive,
    ProjectTfragSourceKind SourceKind,
    int SourceIndex,
    int GroupIndex,
    short OcclusionIndex,
    byte OcclusionIndexStash,
    IReadOnlyList<int> TextureIds,
    ProjectVector3? SourcePosition = null,
    ProjectTfragBounds? Bounds = null,
    ProjectTfragDiagnostics? Diagnostics = null);

public sealed record ProjectTfragState(
    int SchemaVersion,
    IReadOnlyList<ProjectTfragSource> Sources);

internal sealed record ProjectAssetAttachment(
    AssetKind Kind,
    uint CanonicalFormatVersion,
    ReadOnlyMemory<byte> CanonicalBytes,
    AssetId? ParentId = null);
