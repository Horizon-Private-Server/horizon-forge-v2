using Forge.Host.Domain;
using Forge.Host.Games.UYA;

namespace Forge.Host.ProtocolTests.Games.UYA;

internal static class UyaRenderPreparationProgressTests
{
    public static async Task RunAsync()
    {
        var values = new List<IsoProgress>();
        var active = 0;
        using var progress = new UyaRenderPreparationProgress(async value =>
        {
            if (Interlocked.Increment(ref active) != 1) throw new Exception("Progress callbacks overlapped.");
            await Task.Yield();
            values.Add(value);
            Interlocked.Decrement(ref active);
        }, CancellationToken.None);
        await Task.WhenAll(
            Task.Run(async () =>
            {
                for (var index = 1; index <= 100; index++) await progress.VerificationAsync(new(index, 100));
            }),
            Task.Run(async () =>
            {
                for (var index = 2_000; index <= 8_000; index += 100) await progress.PreparationAsync(new(index, 10_000));
            }));
        if (values.Count == 0 || values[^1] != new IsoProgress(8_000, 10_000))
            throw new Exception("Preparation must finish at 80%, reserving publication progress.");
        if (values.Any(value => value.Completed is < 0 or > 8_000 || value.Total != 10_000)
            || values.Zip(values.Skip(1)).Any(pair => pair.First.Completed >= pair.Second.Completed))
            throw new Exception("Concurrent preparation progress regressed or exceeded its bounds.");

        using var cancellation = new CancellationTokenSource();
        using var cancelled = new UyaRenderPreparationProgress(_ => throw new Exception("Cancelled progress was sent"), cancellation.Token);
        cancellation.Cancel();
        try { await cancelled.VerificationAsync(new(1, 1)); }
        catch (OperationCanceledException) { return; }
        throw new Exception("Cancelled progress should stop before invoking its callback.");
    }
}
