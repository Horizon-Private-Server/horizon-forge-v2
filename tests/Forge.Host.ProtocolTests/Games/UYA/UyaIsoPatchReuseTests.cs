using Forge.Host.Games.UYA;
using RatchetPs2.Core.Wad.Models;

namespace Forge.Host.ProtocolTests.Games.UYA;

internal static class UyaIsoPatchReuseTests
{
    public static async Task RunAsync(string source, string target, long isoSize, byte[] packedLevel)
    {
        var initial = await UyaIsoPatchService.PlanAsync(source, target, 3, packedLevel, isoSize);
        await UyaIsoPatchService.ApplyAsync(initial);
        var plan = await UyaIsoPatchService.PlanAsync(source, target, 3, packedLevel, isoSize);
        Check(plan.SdkPlan.Ranges.All(value => value.SourceSha256 == value.OutputSha256),
            "Repeat plan must describe unchanged ranges.");
        var expected = await File.ReadAllBytesAsync(target);
        var sourceBefore = await File.ReadAllBytesAsync(source);
        File.SetLastWriteTimeUtc(target, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var modified = File.GetLastWriteTimeUtc(target);
        var progress = new List<UyaIsoPatchProgress>();
        var result = await UyaIsoPatchService.ApplyAsync(
            plan,
            value => { progress.Add(value); return ValueTask.CompletedTask; },
            _ => throw new InvalidOperationException("Unchanged patch entered the durable write path."),
            CancellationToken.None);
        Check(result.WrittenRanges == 0 && result.Mode == UyaIsoPatchMode.InPlace,
            "Unchanged patch must report zero writes.");
        Check(progress.Count == 1 && progress[0].Phase == "verify", "Unchanged patch must report verification.");
        Check(modified == File.GetLastWriteTimeUtc(target), "Unchanged patch must preserve modification time.");
        await CheckBytesAsync(expected, target, "Unchanged patch altered ISO bytes.");
        Check(!Directory.Exists(UyaIsoPatchJournalStore.PathFor(target)), "Unchanged patch left a journal.");

        // Hash equality is only a shortcut after the SDK validates the complete plan and installed output.
        await ThrowsAsync<InvalidDataException>(() => UyaIsoPatchService.ApplyAsync(
            plan with { SdkPlan = plan.SdkPlan with { Ranges = Array.Empty<IsoPatchRange>() } }));
        var range = plan.SdkPlan.Ranges[0];
        var invalidBytes = range.OutputBytes.ToArray();
        invalidBytes[0] ^= 1;
        await ThrowsAsync<InvalidDataException>(() => UyaIsoPatchService.ApplyAsync(plan with
        {
            SdkPlan = plan.SdkPlan with
            {
                Ranges = plan.SdkPlan.Ranges.Select((value, index) => index == 0
                    ? value with { OutputBytes = invalidBytes } : value).ToArray(),
            },
        }));
        await ThrowsAsync<IOException>(() => UyaIsoPatchService.ApplyAsync(
            plan with { SdkPlan = plan.SdkPlan with { OutputLevelWadSha256 = new string('f', 64) } }));
        await ThrowsAsync<OperationCanceledException>(() => UyaIsoPatchService.ApplyAsync(
            plan, cancellationToken: new CancellationToken(canceled: true)));
        await ThrowsAsync<ArgumentException>(() => UyaIsoPatchService.ApplyAsync(plan with { DevelopmentIsoPath = source }));
        await using (var locked = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await ThrowsAsync<IOException>(() => UyaIsoPatchService.ApplyAsync(plan));

        var journalPath = UyaIsoPatchJournalStore.PathFor(target);
        Directory.CreateDirectory(journalPath);
        try
        {
            await ThrowsAsync<InvalidOperationException>(() => UyaIsoPatchService.ApplyAsync(plan));
            Check(Directory.Exists(journalPath), "Unchanged patch must retain an intervening journal.");
        }
        finally { Directory.Delete(journalPath); }
        await CheckBytesAsync(expected, target, "Rejected plan altered ISO bytes.");
        Check(modified == File.GetLastWriteTimeUtc(target), "Rejected plan modified the ISO.");

        var corrupted = expected.ToArray();
        corrupted[checked((int)range.Offset + 12)] ^= 1;
        await File.WriteAllBytesAsync(target, corrupted);
        await ThrowsAsync<InvalidDataException>(() => UyaIsoPatchService.ApplyAsync(plan));
        await CheckBytesAsync(corrupted, target,
            "Stale plan changed an externally edited target.");
        var repairPlan = await UyaIsoPatchService.PlanAsync(source, target, 3, packedLevel, isoSize);
        var journaled = false;
        var repaired = await UyaIsoPatchService.ApplyAsync(
            repairPlan, progress: null,
            fault => journaled |= fault.Phase == UyaIsoPatchFaultPhase.JournalDurable,
            CancellationToken.None);
        Check(repaired.WrittenRanges > 0 && journaled, "An externally changed ISO must use journaled repair.");
        await CheckBytesAsync(expected, target, "Repair did not restore the expected ISO.");

        var replacedPlan = await UyaIsoPatchService.PlanAsync(source, target, 3, packedLevel, isoSize);
        var replacement = target + ".reuse-test-replacement";
        await File.WriteAllBytesAsync(replacement, expected);
        File.Move(replacement, target, overwrite: true);
        await ThrowsAsync<InvalidDataException>(() => UyaIsoPatchService.ApplyAsync(replacedPlan));
        await CheckBytesAsync(expected, target, "Stale identity plan altered replacement bytes.");
        await CheckBytesAsync(sourceBefore, source, "Reuse checks altered the protected source.");
        Check(!Directory.Exists(journalPath), "Reuse checks left a journal.");
    }

    private static async Task CheckBytesAsync(byte[] expected, string path, string message)
    {
        var actual = await File.ReadAllBytesAsync(path);
        Check(expected.AsSpan().SequenceEqual(actual), message);
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
