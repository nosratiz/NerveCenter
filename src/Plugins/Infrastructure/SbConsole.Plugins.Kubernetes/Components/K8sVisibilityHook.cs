using Microsoft.JSInterop;

namespace SbConsole.Plugins.Kubernetes.Components;

/// <summary>
/// Owns a page's kubernetes.js module import and its <c>watchVisibility</c> listener: the browser reports tab visibility
/// through <see cref="SetHidden"/>, which forwards to the page's callback (typically to set <see cref="K8sPoller.Hidden"/>).
/// <see cref="StartAsync"/> runs once, after first render. <see cref="DisposeAsync"/> may run while it is still in flight:
/// a module or listener handle arriving afterwards is released at once, so the browser never keeps a listener calling a
/// disposed reference. Pages that need the module for other calls (copy, download) use <see cref="Module"/>.
/// </summary>
public sealed class K8sVisibilityHook(Func<bool, Task> onHiddenChanged) : IAsyncDisposable
{
    private DotNetObjectReference<K8sVisibilityHook>? _self;
    private IJSObjectReference? _module;
    private IJSObjectReference? _watch;
    private bool _started;
    private bool _disposed;

    /// <summary>The imported kubernetes.js module, or null before start, after dispose, or when the import failed.</summary>
    public IJSObjectReference? Module => _disposed ? null : _module;

    public async Task StartAsync(IJSRuntime js)
    {
        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        try
        {
            var module = await js.InvokeAsync<IJSObjectReference>("import", K8sJs.ModulePath);
            if (module is null)
            {
                return;
            }

            if (_disposed)
            {
                await module.DisposeAsync();
                return;
            }

            _module = module;
            _self = DotNetObjectReference.Create(this);
            var watch = await module.InvokeAsync<IJSObjectReference>("watchVisibility", _self);
            if (_disposed)
            {
                // DisposeAsync ran while the listener was being installed: release it here or the browser keeps a
                // listener that calls a disposed DotNetObjectReference.
                if (watch is not null)
                {
                    await watch.InvokeVoidAsync("dispose");
                    await watch.DisposeAsync();
                }

                return;
            }

            _watch = watch;
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or TaskCanceledException
                                       or ObjectDisposedException)
        {
            // Without the hook the poller simply keeps running while the tab is hidden.
        }
    }

    /// <summary>Called by kubernetes.js <c>watchVisibility</c>.</summary>
    [JSInvokable]
    public Task SetHidden(bool hidden) => _disposed ? Task.CompletedTask : onHiddenChanged(hidden);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (_watch is not null)
            {
                await _watch.InvokeVoidAsync("dispose");
                await _watch.DisposeAsync();
            }

            if (_module is not null)
            {
                await _module.DisposeAsync();
            }
        }
        catch (Exception ex) when (ex is JSDisconnectedException or JSException or TaskCanceledException)
        {
            // The circuit is already gone; nothing to release.
        }

        _self?.Dispose();
    }
}
