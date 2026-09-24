using Forge.Host.Domain;

namespace Forge.Host.Games.UYA;

public sealed record UyaBuildPatchRequest(
    string ProjectRoot,
    string CatalogRoot,
    string CleanSourceIso,
    string DevelopmentIso,
    string SourceFingerprint,
    IReadOnlySet<string>? AcknowledgedWarnings = null,
    bool ForceFullImage = false,
    IReadOnlySet<BakeLayerId>? IncludedLayers = null);

public sealed record UyaBuildLayerStatus(
    BakeLayerId Layer,
    BakeLayerState State,
    bool CanDefer);

public sealed record UyaBuildPlan(IReadOnlyList<UyaBuildLayerStatus> Layers);

public enum UyaBuildPatchPhase
{
    Preflight,
    Bake,
    Pack,
    Plan,
    Patch,
    Recovery,
    Complete,
}

public sealed record UyaBuildPatchProgress(
    UyaBuildPatchPhase Phase,
    long Completed,
    long Total,
    string Message);

public sealed record UyaBuildPatchResult(
    bool Succeeded,
    bool RequiresWarningAcknowledgement,
    IReadOnlyList<string> WarningCodes,
    IReadOnlyList<string> Diagnostics,
    string Message,
    string NextAction,
    string? DevelopmentIsoPath = null,
    UyaIsoPatchMode? PatchMode = null,
    string? OutputLevelWadSha256 = null,
    int BakedLayerCount = 0,
    bool BakeWasCurrent = false);
