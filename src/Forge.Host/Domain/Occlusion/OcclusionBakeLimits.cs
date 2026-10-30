namespace Forge.Host.Domain.Occlusion;

public static class OcclusionBakeLimits
{
    public const int MaximumCells = RatchetPs2.Core.Occlusion.OcclusionSamplePlanner.MaximumCells;
    public const int MaximumOrigins = RatchetPs2.Core.Occlusion.OcclusionSamplePlanner.MaximumOrigins;
    public const int MaximumSceneBytes = 32 * 1024 * 1024;
    // Base64 sample and cell matrices at these limits fit within 128 MiB.
    public const int MaximumResultBytes = 128 * 1024 * 1024;
}
