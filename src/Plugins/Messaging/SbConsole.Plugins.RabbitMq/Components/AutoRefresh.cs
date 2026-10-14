namespace SbConsole.Plugins.RabbitMq.Components;

/// <summary>
/// The auto-refresh loop every RabbitMQ page shares (AWS Queues.razor's loop, extracted once):
/// owns the CancellationTokenSource, waits <c>interval</c> on the page's TimeProvider, then runs
/// the tick through the component's InvokeAsync. Pages expose the tick itself as
/// <c>internal Task&lt;bool&gt; AutoRefreshTickAsync()</c> so tests call it directly instead of
/// waiting on the real delay. A tick that throws is reported to <c>onError</c> and the loop keeps
/// going; ticks are expected to turn failures into page state, not exceptions.
/// </summary>
public sealed class AutoRefresh(TimeProvider? time = null) : IDisposable
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public bool IsRunning => _cts is not null;

    /// <summary>Starts (or restarts -- a running loop is stopped first) the loop. No-op after Dispose.</summary>
    public void Start(TimeSpan interval, Func<Task> tick, Func<Func<Task>, Task> invokeAsync, Action<Exception>? onError = null)
    {
        Stop();
        if (_disposed)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _ = LoopAsync(interval, tick, invokeAsync, onError, _cts.Token);
    }

    public void Stop()
    {
        if (_cts is { } cts)
        {
            _cts = null;
            cts.Cancel();
            cts.Dispose();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
    }

    private async Task LoopAsync(TimeSpan interval, Func<Task> tick, Func<Func<Task>, Task> invokeAsync, Action<Exception>? onError, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, _time, ct);
                await invokeAsync(tick);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                onError?.Invoke(ex);
            }
        }
    }
}
