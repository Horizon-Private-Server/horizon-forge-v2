using RatchetPs2.Core.Wad.Models;

namespace Forge.Host.Games.UYA;

public sealed record UyaDevelopmentIsoPatchPlan(
    string CleanSourcePath,
    string DevelopmentIsoPath,
    long CleanIsoLength,
    ulong DevelopmentDevice,
    ulong DevelopmentFile,
    UyaIsoIdentity DevelopmentIso,
    IsoPatchPlan SdkPlan);

public sealed record UyaIsoPatchResult(
    string DevelopmentIsoPath,
    int LevelIndex,
    string OutputLevelWadSha256,
    int WrittenRanges,
    UyaIsoPatchMode Mode,
    string StrategyReason);

public sealed record UyaIsoPatchProgress(string Phase, long Completed, long Total, string Message);

public enum UyaIsoPatchMode
{
    InPlace,
    FullImageReplacement,
}

public enum UyaIsoPatchRecoveryState
{
    Original,
    Patched,
    Partial,
    Diverged,
}

public sealed record UyaIsoPatchRecovery(
    string DevelopmentIsoPath,
    int LevelIndex,
    int CompletedRanges,
    int TotalRanges,
    bool WritesComplete,
    UyaIsoPatchRecoveryState State,
    bool CanRestore,
    bool CanAcceptPatchedOutput);

internal enum UyaIsoPatchFaultPhase
{
    JournalDurable,
    RangeDurable,
    CommitAdvanced,
    OutputVerified,
    ReplacementBuilt,
    ReplacementVerified,
}

internal readonly record struct UyaIsoPatchFault(
    UyaIsoPatchFaultPhase Phase,
    int RangeIndex,
    string? ReplacementPath = null);

internal sealed record UyaIsoPatchJournalRange(
    string Name,
    long Offset,
    int Length,
    int Alignment,
    string SourceSha256,
    string OutputSha256,
    string BackupFile);

internal sealed record UyaIsoPatchJournal(
    int SchemaVersion,
    string DocumentType,
    string CleanSourcePath,
    string DevelopmentIsoPath,
    long CleanIsoLength,
    ulong DevelopmentDevice,
    ulong DevelopmentFile,
    long IsoLength,
    int LevelIndex,
    int HeaderSector,
    int PayloadBaseSector,
    int CapacitySectors,
    int RequiredSectors,
    string SourceLevelWadSha256,
    string OutputLevelWadSha256,
    int CompletedRanges,
    bool WritesComplete,
    IReadOnlyList<UyaIsoPatchJournalRange> Ranges);
