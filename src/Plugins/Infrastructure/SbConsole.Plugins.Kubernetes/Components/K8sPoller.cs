namespace SbConsole.Plugins.Kubernetes.Components;

/// <summary>
/// Auto-refresh timer for the plugin pages (spec §11). Every <c>interval</c> it runs <c>tick</c>, unless
/// a dialog has it <see cref="Paused"/>, the browser tab is <see cref="Hidden"/>, or the previous tick
/// is still running (ticks never overlap). Exceptions from the tick are swallowed: the page shows its own
/// stale/error state. Not a component; pages own one and dispose it.
/// </summary>
public sealed class K8sPoller(TimeProvider clock, TimeSpan interval, Func<Task> tick) : IDisposable
{
    private readonly Lock _gate = new();
    private ITimer? _timer;
    private int _inFlight;
    private bool _disposed;

    /// <summary>Set while a dialog is open.</summary>
    public bool Paused { get; set; }

    /// <summary>Set from kubernetes.js <c>watchVisibility</c> (the page's [JSInvokable] SetHidden).</summary>
    public bool Hidden { get; set; }

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _timer is not null)
            {
                return;
            }

            _timer = clock.CreateTimer(_ => OnTimer(), null, interval, interval);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        Stop();
    }

    private void OnTimer()
    {
        if (Paused || Hidden || Volatile.Read(ref _disposed))
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
        {
            return;
        }

        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        try
        {
            await tick();
        }
        catch (Exception)
        {
            // The page renders its own error/stale state; a failed poll must not kill the timer.
        }
        finally
        {
            Volatile.Write(ref _inFlight, 0);
        }
    }
}
