using RatchetPs2.Core.Occlusion;
using System.Numerics;
using System.Text.Json.Serialization;

namespace Forge.Host.Domain.Occlusion;

public sealed record OcclusionGpuTriangle(Vector3 A, Vector3 B, Vector3 C, int Group, uint Flags,
    Vector2 UvA, Vector2 UvB, Vector2 UvC, int Texture, uint WrapFlags, uint Rank);
public sealed record OcclusionGpuTexture(int Width, int Height, byte[] Alpha);
public sealed record OcclusionGpuSphere(float[] Center, float Radius, int Group);
public sealed record OcclusionGpuCheck(float[] Origin, float[] Direction, string Geometry, string Mobys);
public sealed record OcclusionPreparation(int GridBytes, int TreeBytes, int MaskSlots,
    int UnsharedMaskBytes, int AlwaysVisibleGroups, int CellLimit, int SampleLimit);
public sealed record OcclusionScene(
    int SchemaVersion, string Profile, string InputHash, int Triangles,
    string TrianglesHash, string TexturesHash, string AlphaHash,
    IReadOnlyList<float[]> Origins, IReadOnlyList<OcclusionCell> Cells,
    IReadOnlyList<OcclusionGpuSphere> Spheres, IReadOnlyList<OcclusionGpuCheck> Checks, string FallbackMask, int SamplePullIn = 0,
    IReadOnlyList<float>? BarrierHeights = null, bool PixelEdgeAllowance = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OcclusionPreparation? Preparation = null);
public sealed record OcclusionBakeResult(int SchemaVersion, string Profile, string InputHash, string SceneHash,
    int Origins, int Octants, int Batches, double WallMs, double? GpuMs, string SampleMasks);
// Older caches omitted the setting when edge allowance was always enabled; preserve their bake metadata.
public sealed record OcclusionBakeCache(int SchemaVersion, string Profile, string InputHash, string OriginalGridHash,
    string NativeGridHash, byte[] NativeGrid, int Octants, double WallMs, double? GpuMs, int SamplePullIn = 0, bool PixelEdgeAllowance = true,
    OcclusionBakeCoverage? Coverage = null);
// ProjectHash excludes editor hidden/locked flags; exact packed inputs remain bound by InputHash.
public sealed record OcclusionBakeCoverage(string RecipeHash, string ProjectHash, int AlgorithmVersion,
    string CoordinateHash, IReadOnlyList<OcclusionVisibilityEntity> Entities);
