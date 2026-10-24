using Forge.Host.Games.UYA;

namespace Forge.Host.ProtocolTests.Games.UYA;

internal static class UyaPreflightLayerSummaryCacheTests
{
    public static async Task RunAsync()
    {
        var cache = new UyaPreflightLayerSummaryCache();
        var calls = 0;
        int Read(byte key) => cache.GetOrCreate([key], () => ++calls, CancellationToken.None);
        Equal(1, Read(1), "first source is inspected");
        Equal(1, Read(1), "equal bytes reuse a summary");
        Read(2); Read(3); Read(4);
        Equal(1, Read(1), "recent source reuse refreshes retention");
        Read(5);
        Equal(1, Read(1), "recent source survives eviction");
        Equal(6, Read(2), "the fifth source evicts the least recently used summary");

        var source = new byte[] { 10, 20 };
        Equal(10, cache.GetOrCreate(source, () => source[0], CancellationToken.None), "content-based key");
        source[0] = 11;
        Equal(11, cache.GetOrCreate(source, () => source[0], CancellationToken.None), "same-length in-place edit invalidates reuse");
        Equal(10, cache.GetOrCreate([10, 20], () => throw new Exception("Unexpected inspection"), CancellationToken.None),
            "the cache retains a digest rather than a mutable source reference");

        Throws<InvalidDataException>(() => cache.GetOrCreate([100], () => throw new InvalidDataException(), CancellationToken.None));
        Equal(100, cache.GetOrCreate([100], () => 100, CancellationToken.None), "failed inspection is retried");
        using (var cancellation = new CancellationTokenSource())
        {
            Throws<OperationCanceledException>(() => cache.GetOrCreate([101], () =>
            {
                cancellation.Cancel();
                return 101;
            }, cancellation.Token));
            Equal(102, cache.GetOrCreate([101], () => 102, CancellationToken.None), "cancelled inspection is not published");
            Throws<OperationCanceledException>(() => cache.GetOrCreate([101], () => 0, cancellation.Token));
        }

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = Task.Run(() => cache.GetOrCreate([110], () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test inspection was not released.");
            return 110;
        }, CancellationToken.None));
        try
        {
            if (!entered.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test inspection did not begin.");
            using var hitCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            Equal(102, cache.GetOrCreate([101], () => throw new Exception("Cached source was inspected"), hitCancellation.Token),
                "cached counts are available during an unrelated inspection");
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            Throws<OperationCanceledException>(() => cache.GetOrCreate([111], () => throw new Exception("Cancelled waiter ran"), cancellation.Token));
        }
        finally { release.Set(); }
        Equal(110, await first, "cancelling a waiter preserves the active inspection");

        var concurrentCalls = 0;
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            cache.GetOrCreate([120], () => Interlocked.Increment(ref concurrentCalls), CancellationToken.None))));
        Equal(true, results.All(result => result == 1), "concurrent consumers reuse one successful inspection");
        Equal(1, concurrentCalls, "concurrent cold source is inspected once");
    }

    private static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{context}: expected {expected}, got {actual}");
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}");
    }
}
