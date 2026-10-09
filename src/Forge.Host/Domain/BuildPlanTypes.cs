namespace Forge.Host.Domain;

public sealed record BuildLayerStatus(BakeLayerId Layer, BakeLayerState State, bool CanDefer);

public sealed record BuildPlan(IReadOnlyList<BuildLayerStatus> Layers);
