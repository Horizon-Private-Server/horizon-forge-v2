namespace Forge.Host.Domain;

public enum BakePhase
{
    Preflight,
    Staging,
    Validate,
    Complete,
}

public sealed record BakeProgress(
    BakePhase Phase,
    BakeLayerId? Layer,
    int CompletedLayers,
    int TotalLayers,
    string Message);

public sealed record BakeResult(
    bool Succeeded,
    bool IsCurrent,
    BakeValidationResult Validation,
    BakeManifest Manifest,
    IReadOnlyList<BakeLayerSnapshot> WrittenLayers,
    PaletteBakeReport? PaletteReport = null);
