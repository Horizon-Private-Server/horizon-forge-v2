namespace Forge.Host.Domain.Occlusion;

public sealed record OcclusionVisibilityOctant(int X, int Y, int Z, int MaskIndex);
public sealed record OcclusionVisibilityEntity(string Id, IReadOnlyList<int> Groups);
public sealed record OcclusionVisibilityData(int SchemaVersion, string Source, string GridHash,
    IReadOnlyList<OcclusionVisibilityOctant> Octants, byte[] Masks, IReadOnlyList<OcclusionVisibilityEntity> Entities);
