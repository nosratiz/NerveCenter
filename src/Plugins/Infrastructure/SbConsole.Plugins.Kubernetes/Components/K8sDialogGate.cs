namespace SbConsole.Plugins.Kubernetes.Components;

/// <summary>
/// Runs dialogs with auto-refresh paused, so a poll never repaints the page under an open dialog. Counts nested
/// dialogs: the pause lifts only when the last one closes, and is restored even when the dialog throws.
/// Not thread-safe; use it from the renderer's context.
/// </summary>
public sealed class K8sDialogGate(Action<bool> setPaused)
{
    private int _open;

    public K8sDialogGate(K8sPoller poller)
        : this(paused => poller.Paused = paused)
    {
    }

    /// <summary>True while at least one dialog is open.</summary>
    public bool IsOpen => _open > 0;

    public async Task<T> RunAsync<T>(Func<Task<T>> showDialog)
    {
        _open++;
        setPaused(true);
        try
        {
            return await showDialog();
        }
        finally
        {
            _open--;
            setPaused(_open > 0);
        }
    }

    public Task RunAsync(Func<Task> showDialog) => RunAsync(async () =>
    {
        await showDialog();
        return true;
    });
}
