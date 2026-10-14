using FluentAssertions;
using Microsoft.JSInterop;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.Kubernetes.Components;

namespace SbConsole.Plugins.Kubernetes.Tests.Components;

public class K8sVisibilityHookTests
{
    private readonly IJSRuntime _js = Substitute.For<IJSRuntime>();
    private readonly IJSObjectReference _module = Substitute.For<IJSObjectReference>();
    private readonly IJSObjectReference _handle = Substitute.For<IJSObjectReference>();
    private readonly List<bool> _hidden = [];

    public K8sVisibilityHookTests()
    {
        _js.InvokeAsync<IJSObjectReference>("import", Arg.Any<object?[]?>()).Returns(new ValueTask<IJSObjectReference>(_module));
        _module.InvokeAsync<IJSObjectReference>("watchVisibility", Arg.Any<object?[]?>()).Returns(new ValueTask<IJSObjectReference>(_handle));
    }

    private K8sVisibilityHook Hook() => new(hidden => { _hidden.Add(hidden); return Task.CompletedTask; });

    private static bool Called(IJSObjectReference reference, string identifier) =>
        reference.ReceivedCalls().Any(c => Equals(c.GetArguments().FirstOrDefault(), identifier));

    [Fact]
    public async Task Start_imports_the_module_and_watches_visibility_with_a_reference_to_itself()
    {
        var hook = Hook();

        await hook.StartAsync(_js);

        await _js.Received(1).InvokeAsync<IJSObjectReference>("import", Arg.Is<object?[]?>(a => Equals(a![0], K8sJs.ModulePath)));
        await _module.Received(1).InvokeAsync<IJSObjectReference>("watchVisibility",
            Arg.Is<object?[]?>(a => a![0] is DotNetObjectReference<K8sVisibilityHook>));
        hook.Module.Should().BeSameAs(_module);
    }

    [Fact]
    public async Task Start_is_a_no_op_the_second_time()
    {
        var hook = Hook();

        await hook.StartAsync(_js);
        await hook.StartAsync(_js);

        await _js.Received(1).InvokeAsync<IJSObjectReference>("import", Arg.Any<object?[]?>());
    }

    [Fact]
    public async Task SetHidden_forwards_to_the_callback_until_disposed()
    {
        var hook = Hook();
        await hook.StartAsync(_js);

        await hook.SetHidden(true);
        await hook.SetHidden(false);
        await hook.DisposeAsync();
        await hook.SetHidden(true);

        _hidden.Should().Equal(true, false);
    }

    [Fact]
    public async Task Dispose_releases_the_listener_and_the_module()
    {
        var hook = Hook();
        await hook.StartAsync(_js);

        await hook.DisposeAsync();

        Called(_handle, "dispose").Should().BeTrue();
        await _handle.Received(1).DisposeAsync();
        await _module.Received(1).DisposeAsync();
        hook.Module.Should().BeNull();
    }

    [Fact]
    public async Task A_visibility_handle_resolving_after_dispose_is_released_at_once()
    {
        var watch = new TaskCompletionSource<IJSObjectReference>();
        _module.InvokeAsync<IJSObjectReference>("watchVisibility", Arg.Any<object?[]?>()).Returns(_ => new ValueTask<IJSObjectReference>(watch.Task));
        var hook = Hook();

        var start = hook.StartAsync(_js);
        await hook.DisposeAsync();
        watch.SetResult(_handle);
        await start;

        Called(_handle, "dispose").Should().BeTrue();
        await _handle.Received(1).DisposeAsync();
        await _module.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task A_module_resolving_after_dispose_is_released_without_watching()
    {
        var import = new TaskCompletionSource<IJSObjectReference>();
        _js.InvokeAsync<IJSObjectReference>("import", Arg.Any<object?[]?>()).Returns(_ => new ValueTask<IJSObjectReference>(import.Task));
        var hook = Hook();

        var start = hook.StartAsync(_js);
        await hook.DisposeAsync();
        import.SetResult(_module);
        await start;

        await _module.Received(1).DisposeAsync();
        Called(_module, "watchVisibility").Should().BeFalse();
        hook.Module.Should().BeNull();
    }

    [Fact]
    public async Task A_disconnected_circuit_is_tolerated_on_start_and_dispose()
    {
        _js.InvokeAsync<IJSObjectReference>("import", Arg.Any<object?[]?>()).ThrowsAsync(new JSDisconnectedException("gone"));
        var hook = Hook();

        await hook.StartAsync(_js);
        hook.Module.Should().BeNull();

        var other = Hook();
        _js.InvokeAsync<IJSObjectReference>("import", Arg.Any<object?[]?>()).Returns(new ValueTask<IJSObjectReference>(_module));
        await other.StartAsync(_js);
        _handle.DisposeAsync().ThrowsAsync(new JSDisconnectedException("gone"));
        var dispose = async () => await other.DisposeAsync();

        await dispose.Should().NotThrowAsync();
    }
}
