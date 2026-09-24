using Forge.Host.Domain;

namespace Forge.Host.Games.UYA;

public enum UyaBakePhase
{
    Preflight,
    Staging,
    Validate,
    Complete,
}

public sealed record UyaBakeProgress(
    UyaBakePhase Phase,
    BakeLayerId? Layer,
    int CompletedLayers,
    int TotalLayers,
    string Message);

public sealed record UyaBakeResult(
    bool Succeeded,
    bool IsCurrent,
    BakeValidationResult Validation,
    BakeManifest Manifest,
    IReadOnlyList<BakeLayerSnapshot> WrittenLayers,
    PaletteBakeReport? PaletteReport = null);
