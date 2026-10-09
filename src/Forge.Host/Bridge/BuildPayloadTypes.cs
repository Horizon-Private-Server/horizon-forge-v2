namespace Forge.Host.Bridge;

public sealed record UyaBuildPatchRequestPayload(
    string ProjectRoot,
    string CatalogRoot,
    string CleanSourceIso,
    string DevelopmentIso,
    string SourceFingerprint,
    IReadOnlyList<string> AcknowledgedWarnings,
    bool ForceFullImage,
    IReadOnlyList<string> IncludedLayers,
    bool ForceInPlace);

public sealed record BuildPlanRequestPayload(string ProjectRoot, string CatalogRoot);

public sealed record BuildLayerStatusPayload(string Layer, string State, bool CanDefer);

public sealed record BuildPlanPayload(IReadOnlyList<BuildLayerStatusPayload> Layers);

public sealed record UyaBuildPatchProgressPayload(
    string Phase,
    ulong Completed,
    ulong Total,
    string Message);

public sealed record UyaBuildPatchResultPayload(
    bool Succeeded,
    bool RequiresWarningAcknowledgement,
    IReadOnlyList<string> WarningCodes,
    IReadOnlyList<string> Diagnostics,
    string Message,
    string NextAction,
    string DevelopmentIsoPath,
    string PatchMode,
    string OutputLevelWadSha256,
    uint BakedLayerCount,
    bool BakeWasCurrent);
