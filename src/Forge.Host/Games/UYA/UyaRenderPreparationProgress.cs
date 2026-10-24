using Forge.Host.Domain;

namespace Forge.Host.Games.UYA;

internal sealed class UyaRenderPreparationProgress(
    Func<IsoProgress, ValueTask>? progress,
    CancellationToken cancellationToken) : IDisposable
{
    private readonly SemaphoreSlim _report = new(1, 1);
    private long _verified;
    private long _prepared;
    private long _reported;

    public ValueTask VerificationAsync(IsoProgress value)
    {
        Interlocked.Exchange(ref _verified, value.Total <= 0 ? 0
            : Math.Clamp(value.Completed * 6_000 / value.Total, 0, 6_000));
        return PublishAsync();
    }

    public ValueTask PreparationAsync(IsoProgress value)
    {
        Interlocked.Exchange(ref _prepared, Math.Clamp(value.Completed - 2_000, 0, 6_000) / 3);
        return PublishAsync();
    }

    private async ValueTask PublishAsync()
    {
        if (progress is null) return;
        await _report.WaitAsync(cancellationToken);
        try
        {
            var completed = Volatile.Read(ref _verified) + Volatile.Read(ref _prepared);
            if (completed <= _reported) return;
            await progress(new(completed, 10_000));
            _reported = completed;
        }
        finally { _report.Release(); }
    }

    public void Dispose() => _report.Dispose();
}
