using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using SbConsole.Plugins.Kubernetes.Components;

namespace SbConsole.Plugins.Kubernetes.Tests.Components;

public class K8sDialogGateTests
{
    private readonly FakeTimeProvider _time = new();

    private K8sPoller Poller() => new(_time, TimeSpan.FromSeconds(5), () => Task.CompletedTask);

    [Fact]
    public async Task Pauses_the_poller_while_a_dialog_is_open_and_returns_its_result()
    {
        using var poller = Poller();
        var gate = new K8sDialogGate(poller);
        var dialog = new TaskCompletionSource<int>();

        var run = gate.RunAsync(() => dialog.Task);
        poller.Paused.Should().BeTrue();
        gate.IsOpen.Should().BeTrue();

        dialog.SetResult(7);
        (await run).Should().Be(7);
        poller.Paused.Should().BeFalse();
        gate.IsOpen.Should().BeFalse();
    }

    [Fact]
    public async Task Nested_dialogs_keep_the_poller_paused_until_the_last_one_closes()
    {
        using var poller = Poller();
        var gate = new K8sDialogGate(poller);
        var outer = new TaskCompletionSource<bool>();
        var inner = new TaskCompletionSource<bool>();

        var first = gate.RunAsync(() => outer.Task);
        var second = gate.RunAsync(() => inner.Task);

        inner.SetResult(true);
        await second;
        poller.Paused.Should().BeTrue();
        gate.IsOpen.Should().BeTrue();

        outer.SetResult(true);
        await first;
        poller.Paused.Should().BeFalse();
        gate.IsOpen.Should().BeFalse();
    }

    [Fact]
    public async Task A_throwing_dialog_still_restores_the_pause_state()
    {
        using var poller = Poller();
        var gate = new K8sDialogGate(poller);

        var act = () => gate.RunAsync(() => Task.FromException(new InvalidOperationException("boom")));

        await act.Should().ThrowAsync<InvalidOperationException>();
        poller.Paused.Should().BeFalse();
        gate.IsOpen.Should().BeFalse();
    }

    [Fact]
    public async Task Works_with_a_pause_callback_instead_of_a_poller()
    {
        var states = new List<bool>();
        var gate = new K8sDialogGate(states.Add);

        await gate.RunAsync(() => Task.CompletedTask);

        states.Should().Equal(true, false);
    }
}
