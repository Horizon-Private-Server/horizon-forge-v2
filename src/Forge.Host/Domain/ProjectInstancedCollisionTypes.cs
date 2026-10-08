using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Forge.Host.Domain;

public enum ProjectInstancedCollisionRecipeKind
{
    Surface,
    Wrap,
    Hull,
}

public sealed record ProjectInstancedCollisionRecipe(
    ProjectInstancedCollisionRecipeKind Kind,
    int GeneratorVersion,
    int RecipeVersion,
    int LodIndex,
    byte RawType,
    float DetailSize = 0,
    float SealOpeningSize = 0,
    float SurfaceOffset = 0,
    bool OpenBase = false,
    int ProfileSections = 0);

public sealed record ProjectCollisionFaceTypeOverride(int FaceIndex, byte RawType);

public sealed record ProjectInstancedCollisionBinding(
    AssetId SourceAssetId,
    AssetId ProxyAssetId,
    ProjectInstancedCollisionRecipe Recipe,
    IReadOnlyList<ProjectCollisionFaceTypeOverride> FaceTypeOverrides,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] EntityId? InstanceEntityId = null);

internal static class ProjectInstancedCollisionExtensions
{
    public static bool IsInstancedCollisionSource(
        [NotNullWhen(true)] this ProjectAssetReference? asset) =>
        asset?.Kind is AssetKind.Tie or AssetKind.Shrub;
}
