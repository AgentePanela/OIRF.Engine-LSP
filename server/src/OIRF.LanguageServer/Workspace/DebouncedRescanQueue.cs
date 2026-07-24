namespace OIRF.LanguageServer.Workspace;

/// <summary>
/// Coalesces bursts of "something changed, rescan" triggers (e.g. many .cs files saved by a
/// bulk git operation) into a single action after a quiet period.
/// </summary>
public sealed class DebouncedRescanQueue(TimeSpan delay, Func<CancellationToken, Task> action) : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _pending;

    public void Trigger()
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            _pending?.Cancel();
            _pending?.Dispose();
            cts = new CancellationTokenSource();
            _pending = cts;
        }

        _ = RunAfterDelayAsync(cts);
    }

    private async Task RunAfterDelayAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(delay, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (cts.IsCancellationRequested)
            return;

        await action(cts.Token);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _pending?.Cancel();
            _pending?.Dispose();
        }
    }
}
