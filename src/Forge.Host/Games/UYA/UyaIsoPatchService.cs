using Forge.Host.Domain;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.Wad.Models;
using RatchetPs2.Games.UYA.Level;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.UYA;

public static class UyaIsoPatchService
{
    public static async Task<UyaDevelopmentIsoPatchPlan> PlanAsync(
        string cleanSourcePath,
        string developmentIsoPath,
        int levelIndex,
        ReadOnlyMemory<byte> packedLevelWad,
        long? expectedIsoSize = null,
        bool forceFullImage = false,
        CancellationToken cancellationToken = default)
    {
        var isoSize = expectedIsoSize ?? UyaIsoService.SupportedSize;
        if (packedLevelWad.IsEmpty) throw new ArgumentException("Packed level WAD cannot be empty.", nameof(packedLevelWad));
        if (Directory.Exists(UyaIsoPatchJournalStore.PathFor(developmentIsoPath)))
            throw new InvalidOperationException("The development ISO has an unfinished patch journal; recover it first.");
        var opened = OpenProtected(cleanSourcePath, developmentIsoPath, isoSize);
        await using var source = opened.Source;
        await using var target = opened.Target;
        var sdkPlan = await Task.Run(() =>
        {
            var cleanPlan = IsoPatchPlanner.Create(GameId.UYA,
                source, levelIndex, packedLevelWad.Span, forceFullImage, cancellationToken);
            return cleanPlan.FitsInPlace
                ? IsoPatchPlanner.Create(GameId.UYA,
                    target,
                    cleanPlan.Allocation,
                    packedLevelWad.Span,
                    cancellationToken: cancellationToken)
                : IsoPatchPlanner.Create(GameId.UYA,
                    target, levelIndex, packedLevelWad.Span, forceFullImage, cancellationToken);
        }, cancellationToken);
        return new(
            opened.CleanPath,
            opened.DevelopmentPath,
            source.Length,
            opened.Identity.Device,
            opened.Identity.File,
            opened.Disc,
            sdkPlan);
    }

    public static Task<UyaIsoPatchResult> ApplyAsync(
        UyaDevelopmentIsoPatchPlan plan,
        Func<UyaIsoPatchProgress, ValueTask>? progress = null,
        CancellationToken cancellationToken = default) =>
        ApplyAsync(plan, progress, null, cancellationToken);

    internal static async Task<UyaIsoPatchResult> ApplyAsync(
        UyaDevelopmentIsoPatchPlan plan,
        Func<UyaIsoPatchProgress, ValueTask>? progress,
        Action<UyaIsoPatchFault>? fault,
        CancellationToken cancellationToken,
        long? availableBytes = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.SdkPlan.FitsInPlace)
            return await ApplyReplacementAsync(plan, progress, fault, availableBytes, cancellationToken);
        var opened = OpenProtected(plan.CleanSourcePath, plan.DevelopmentIsoPath, plan.CleanIsoLength);
        await using var source = opened.Source;
        await using var target = opened.Target;
        EnsurePlannedTarget(plan, opened);
        await Task.Run(() => IsoPatchApplier.ValidateSource(GameId.UYA, target, plan.SdkPlan, cancellationToken), cancellationToken);
        var journal = await UyaIsoPatchJournalStore.CreateAsync(plan, target, cancellationToken);
        fault?.Invoke(new(UyaIsoPatchFaultPhase.JournalDurable, -1));

        for (var index = 0; index < plan.SdkPlan.Ranges.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rangeIndex = index;
            await Task.Run(
                () => IsoPatchApplier.ApplyRange(GameId.UYA, target, plan.SdkPlan, rangeIndex),
                CancellationToken.None);
            target.Flush(flushToDisk: true);
            IsoPatchApplier.VerifyRangeOutput(GameId.UYA, target, plan.SdkPlan, index);
            fault?.Invoke(new(UyaIsoPatchFaultPhase.RangeDurable, index));
            journal = journal with { CompletedRanges = index + 1 };
            await UyaIsoPatchJournalStore.UpdateAsync(journal, CancellationToken.None);
            fault?.Invoke(new(UyaIsoPatchFaultPhase.CommitAdvanced, index));
            if (progress is not null)
                await progress(new(
                    "write", index + 1, plan.SdkPlan.Ranges.Count, $"Patched {plan.SdkPlan.Ranges[index].Name}."));
        }

        await Task.Run(() => IsoPatchApplier.VerifyOutput(GameId.UYA, target, plan.SdkPlan), CancellationToken.None);
        var identity = UyaIsoService.InspectDevelopment(target, plan.SdkPlan.IsoLength);
        if (!identity.IsSupported) throw new InvalidDataException(identity.Diagnostic);
        IsoPatchApplier.VerifyInstalledLevel(GameId.UYA, target, plan.SdkPlan);
        journal = journal with { WritesComplete = true };
        await UyaIsoPatchJournalStore.UpdateAsync(journal, CancellationToken.None);
        fault?.Invoke(new(UyaIsoPatchFaultPhase.OutputVerified, plan.SdkPlan.Ranges.Count - 1));
        UyaIsoPatchJournalStore.Delete(plan.DevelopmentIsoPath);
        return new(
            plan.DevelopmentIsoPath,
            plan.SdkPlan.LevelIndex,
            plan.SdkPlan.OutputLevelWadSha256,
            plan.SdkPlan.Ranges.Count,
            UyaIsoPatchMode.InPlace,
            plan.SdkPlan.StrategyReason);
    }

    private static async Task<UyaIsoPatchResult> ApplyReplacementAsync(
        UyaDevelopmentIsoPatchPlan plan,
        Func<UyaIsoPatchProgress, ValueTask>? progress,
        Action<UyaIsoPatchFault>? fault,
        long? availableBytes,
        CancellationToken cancellationToken)
    {
        var replacement = plan.SdkPlan.Replacement
            ?? throw new InvalidDataException("The full-image patch plan has no replacement layout.");
        var directory = Path.GetDirectoryName(plan.DevelopmentIsoPath)!;
        var temporary = Path.Combine(directory, $".{Path.GetFileName(plan.DevelopmentIsoPath)}.forge-replacement");
        UyaIsoService.EnsureEnoughSpace(
            replacement.RequiredFreeBytes,
            availableBytes ?? UyaIsoService.GetAvailableSpace(directory));
        if (progress is not null)
            await progress(new("fallback", 0, replacement.OutputIsoLength, plan.SdkPlan.StrategyReason));
        File.Delete(temporary);
        try
        {
            var opened = OpenProtected(plan.CleanSourcePath, plan.DevelopmentIsoPath, plan.CleanIsoLength);
            await using (opened.Source)
            await using (opened.Target)
            {
                EnsurePlannedTarget(plan, opened);
                await using (var output = new FileStream(
                    temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
                {
                    await IsoReplacementBuilder.BuildAsync(GameId.UYA, 
                        opened.Target,
                        output,
                        plan.SdkPlan,
                        progress is null
                            ? null
                            : (completed, total) => progress(new(
                                "copy", completed, total, "Building full development ISO replacement.")),
                        cancellationToken);
                    await output.FlushAsync(cancellationToken);
                    output.Flush(flushToDisk: true);
                }

                fault?.Invoke(new(UyaIsoPatchFaultPhase.ReplacementBuilt, -1, temporary));
                await using (var verification = new FileStream(
                    temporary, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.RandomAccess))
                {
                    var identity = UyaIsoService.InspectDevelopment(verification, replacement.OutputIsoLength);
                    if (!identity.IsSupported) throw new InvalidDataException(identity.Diagnostic);
                    IsoReplacementBuilder.Verify(GameId.UYA, verification, plan.SdkPlan);
                }
                fault?.Invoke(new(UyaIsoPatchFaultPhase.ReplacementVerified, -1, temporary));
            }

            EnsureDestinationUnchanged(plan);
            UyaIsoService.Commit(temporary, plan.DevelopmentIsoPath);
            return new(
                plan.DevelopmentIsoPath,
                plan.SdkPlan.LevelIndex,
                plan.SdkPlan.OutputLevelWadSha256,
                0,
                UyaIsoPatchMode.FullImageReplacement,
                plan.SdkPlan.StrategyReason);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public static async Task<UyaIsoPatchRecovery?> InspectRecoveryAsync(
        string developmentIsoPath,
        CancellationToken cancellationToken = default)
    {
        var journal = await UyaIsoPatchJournalStore.LoadAsync(developmentIsoPath, cancellationToken);
        if (journal is null) return null;
        await using var target = new FileStream(
            UyaIsoService.ResolvePath(developmentIsoPath), FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            64 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
        EnsureJournalTarget(journal, target);
        var hashes = new string[journal.Ranges.Count];
        for (var index = 0; index < hashes.Length; index++)
        {
            var range = journal.Ranges[index];
            hashes[index] = await Task.Run(
                () => IsoPatchApplier.HashRange(GameId.UYA, target, range.Offset, range.Length, cancellationToken),
                cancellationToken);
        }
        var allSource = hashes.Where((value, index) => value == journal.Ranges[index].SourceSha256).Count() == hashes.Length;
        var allOutput = hashes.Where((value, index) => value == journal.Ranges[index].OutputSha256).Count() == hashes.Length;
        var allKnown = hashes.Where((value, index) =>
            value == journal.Ranges[index].SourceSha256 || value == journal.Ranges[index].OutputSha256).Count() == hashes.Length;
        var state = allSource
            ? UyaIsoPatchRecoveryState.Original
            : allOutput
                ? UyaIsoPatchRecoveryState.Patched
                : allKnown
                    ? UyaIsoPatchRecoveryState.Partial
                    : UyaIsoPatchRecoveryState.Diverged;
        return new(
            journal.DevelopmentIsoPath,
            journal.LevelIndex,
            journal.CompletedRanges,
            journal.Ranges.Count,
            journal.WritesComplete,
            state,
            true,
            state == UyaIsoPatchRecoveryState.Patched);
    }

    public static async Task RecoverAsync(
        string developmentIsoPath,
        CancellationToken cancellationToken = default)
    {
        var journal = await UyaIsoPatchJournalStore.LoadAsync(developmentIsoPath, cancellationToken)
            ?? throw new InvalidOperationException("The development ISO has no patch journal to recover.");
        var opened = OpenProtected(
            journal.CleanSourcePath,
            journal.DevelopmentIsoPath,
            journal.CleanIsoLength);
        await using var source = opened.Source;
        await using var target = opened.Target;
        EnsureJournalTarget(journal, target);
        var ranges = new List<IsoPatchRange>(journal.Ranges.Count);
        foreach (var range in journal.Ranges)
        {
            var backup = await UyaIsoPatchJournalStore.ReadBackupAsync(
                journal.DevelopmentIsoPath, range, cancellationToken);
            var current = await Task.Run(
                () => IsoPatchApplier.HashRange(GameId.UYA, target, range.Offset, range.Length, cancellationToken),
                cancellationToken);
            ranges.Add(new(
                range.Name,
                range.Offset,
                range.Length,
                range.Alignment,
                current,
                range.SourceSha256,
                backup));
        }
        var recoveryPlan = new IsoPatchPlan(
            IsoPatchPlanner.SchemaVersion,
            journal.LevelIndex,
            journal.IsoLength,
            journal.HeaderSector,
            journal.PayloadBaseSector,
            journal.CapacitySectors,
            journal.RequiredSectors,
            true,
            journal.OutputLevelWadSha256,
            journal.SourceLevelWadSha256,
            ranges,
            "Recovering the preimage recorded by the interrupted in-place patch.",
            null);
        IsoPatchApplier.ValidateSource(GameId.UYA, target, recoveryPlan, cancellationToken);
        for (var index = 0; index < ranges.Count; index++)
        {
            IsoPatchApplier.ApplyRange(GameId.UYA, target, recoveryPlan, index, cancellationToken);
            target.Flush(flushToDisk: true);
            IsoPatchApplier.VerifyRangeOutput(GameId.UYA, target, recoveryPlan, index, cancellationToken);
        }
        IsoPatchApplier.VerifyOutput(GameId.UYA, target, recoveryPlan, cancellationToken);
        UyaIsoPatchJournalStore.Delete(developmentIsoPath);
    }

    public static async Task AcceptPatchedRecoveryAsync(
        string developmentIsoPath,
        CancellationToken cancellationToken = default)
    {
        var recovery = await InspectRecoveryAsync(developmentIsoPath, cancellationToken)
            ?? throw new InvalidOperationException("The development ISO has no patch journal to accept.");
        if (!recovery.CanAcceptPatchedOutput)
            throw new InvalidDataException("The interrupted ISO does not contain the complete verified patch result.");
        UyaIsoPatchJournalStore.Delete(developmentIsoPath);
    }

    private static ProtectedIso OpenProtected(
        string cleanSourcePath,
        string developmentIsoPath,
        long expectedIsoSize)
    {
        var clean = UyaIsoService.ResolvePath(cleanSourcePath);
        var development = UyaIsoService.ResolvePath(developmentIsoPath);
        if (UyaIsoService.PathEquals(clean, development))
            throw new ArgumentException("The clean source ISO cannot be used as the development patch target.");
        var source = new FileStream(clean, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.RandomAccess);
        FileStream? target = null;
        try
        {
            var sourceDisc = UyaIsoService.InspectDevelopment(source, expectedIsoSize);
            if (!sourceDisc.IsSupported)
                throw new InvalidDataException($"Clean source protection failed: {sourceDisc.Diagnostic}");
            using var probe = new FileStream(
                development, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.RandomAccess);
            var sourceIdentity = FileIdentity.Read(source.SafeFileHandle);
            var targetIdentity = FileIdentity.Read(probe.SafeFileHandle);
            if (sourceIdentity == targetIdentity)
                throw new ArgumentException("The development ISO aliases the protected clean source filesystem object.");
            target = new FileStream(
                development, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 64 * 1024, FileOptions.RandomAccess);
            if (FileIdentity.Read(target.SafeFileHandle) != targetIdentity)
                throw new IOException("The development ISO changed while it was being opened.");
            var targetDisc = UyaIsoService.InspectDevelopment(target, expectedIsoSize, allowLarger: true);
            if (!targetDisc.IsSupported) throw new InvalidDataException(targetDisc.Diagnostic);
            return new(clean, development, source, target, targetIdentity, targetDisc);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            target?.Dispose();
            source.Dispose();
            throw new IOException(
                "The development ISO is locked, not writable, or aliases the protected clean source. "
                + "Close/eject it from PCSX2 and retry.", exception);
        }
        catch
        {
            target?.Dispose();
            source.Dispose();
            throw;
        }
    }

    private static void EnsurePlannedTarget(UyaDevelopmentIsoPatchPlan plan, ProtectedIso opened)
    {
        if (!UyaIsoService.PathEquals(plan.CleanSourcePath, opened.CleanPath)
            || !UyaIsoService.PathEquals(plan.DevelopmentIsoPath, opened.DevelopmentPath)
            || plan.DevelopmentDevice != opened.Identity.Device
            || plan.DevelopmentFile != opened.Identity.File)
            throw new InvalidDataException("The development ISO no longer matches its patch plan.");
    }

    private static void EnsureJournalTarget(UyaIsoPatchJournal journal, FileStream target)
    {
        var identity = FileIdentity.Read(target.SafeFileHandle);
        if (target.Length != journal.IsoLength
            || identity.Device != journal.DevelopmentDevice
            || identity.File != journal.DevelopmentFile)
            throw new InvalidDataException("The development ISO no longer matches its recovery journal.");
    }

    private static void EnsureDestinationUnchanged(UyaDevelopmentIsoPatchPlan plan)
    {
        using var target = new FileStream(
            plan.DevelopmentIsoPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.RandomAccess);
        var identity = FileIdentity.Read(target.SafeFileHandle);
        if (identity.Device != plan.DevelopmentDevice || identity.File != plan.DevelopmentFile)
            throw new InvalidDataException("The development ISO changed before replacement commit.");
    }

    private sealed record ProtectedIso(
        string CleanPath,
        string DevelopmentPath,
        FileStream Source,
        FileStream Target,
        FileIdentity Identity,
        UyaIsoIdentity Disc);
}
