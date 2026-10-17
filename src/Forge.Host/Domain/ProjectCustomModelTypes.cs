namespace Forge.Host.Domain;

public static class ProjectCustomModelSchema
{
    public const int CurrentVersion = 2;
    public const uint SourceFormatVersion = 1;
    public const uint CanonicalFormatVersion = 2;
    public const uint NativeFormatVersion = 1;
    public const int MaximumModels = 16_384;
    public const int MaximumDependencies = 64;
}

public enum ProjectCustomModelDependencyKind
{
    Buffer = 1,
    Image = 2,
}

public sealed record ProjectCustomModelImportFile(string Uri, byte[] Bytes);

public sealed record ProjectCustomModelImportRequest(
    string SourceName,
    byte[] SourceBytes,
    IReadOnlyList<ProjectCustomModelImportFile> Dependencies,
    bool ForceBackfaceCulling = false);

public sealed record ProjectCustomModelDependency(
    string Uri,
    ProjectCustomModelDependencyKind Kind,
    ProjectAssetReference Asset);

public sealed record ProjectCustomModelMaterialBinding(
    int NativeMaterialIndex,
    int? SourceMaterialIndex,
    ProjectAssetReference Texture,
    bool ClampU,
    bool ClampV);

public sealed record ProjectCustomModelBounds(
    float MinimumX,
    float MinimumY,
    float MinimumZ,
    float MaximumX,
    float MaximumY,
    float MaximumZ);

public sealed record ProjectCustomModelPrimitiveBinding(
    int SourceMeshIndex,
    int SourcePrimitiveIndex,
    int NativeMaterialIndex,
    int FirstTriangle,
    int TriangleCount,
    int FirstPacket,
    int PacketCount);

public sealed record ProjectCustomModelMetadata(
    int VertexCount,
    int TriangleCount,
    int PacketCount,
    int MaterialCount,
    int TextureCount,
    int PaletteCount,
    ProjectCustomModelBounds Bounds,
    string LodPolicy,
    IReadOnlyList<ProjectCustomModelPrimitiveBinding> Primitives,
    IReadOnlyList<string> Diagnostics);

public sealed record ProjectCustomModel(
    int SchemaVersion,
    string SourceName,
    string SourceSha256,
    ProjectAssetReference Source,
    IReadOnlyList<ProjectCustomModelDependency> Dependencies,
    ProjectAssetReference Canonical,
    ProjectAssetReference Native,
    IReadOnlyList<ProjectAssetReference> Textures,
    IReadOnlyList<ProjectCustomModelMaterialBinding> Materials,
    ProjectCustomModelMetadata Metadata);

public sealed record ProjectCustomModelState(
    int SchemaVersion,
    IReadOnlyList<ProjectCustomModel> Models)
{
    public static ProjectCustomModelState Empty { get; } = new(
        ProjectCustomModelSchema.CurrentVersion,
        []);
}

public sealed record ProjectCustomModelDeleteReport(
    AssetId ModelAssetId,
    IReadOnlyList<EntityId> ClassInstances,
    IReadOnlyList<AssetId> OwnedAssets,
    IReadOnlyList<string> AssetDependents)
{
    public bool IsBlocked => ClassInstances.Count > 0 || AssetDependents.Count > 0;
}

public sealed class ProjectCustomModelDeleteBlockedException(ProjectCustomModelDeleteReport report)
    : InvalidOperationException(
        $"Custom model {report.ModelAssetId} cannot be deleted while referenced by "
        + $"{report.ClassInstances.Count} class instance(s) and {report.AssetDependents.Count} asset dependent(s).")
{
    public ProjectCustomModelDeleteReport Report { get; } = report;
}
