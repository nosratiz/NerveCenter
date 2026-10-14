using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using SbConsole.Plugins.Kubernetes.Components;

namespace SbConsole.Plugins.Kubernetes.Tests.Components;

public class K8sPollerTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));
    private int _ticks;

    private K8sPoller Poller(Func<Task>? tick = null) =>
        new(_time, Interval, tick ?? (() => { _ticks++; return Task.CompletedTask; }));

    [Fact]
    public void Ticks_once_per_interval_after_start_and_not_before()
    {
        using var poller = Poller();
        _time.Advance(Interval);
        _ticks.Should().Be(0);

        poller.Start();
        _time.Advance(Interval - TimeSpan.FromSeconds(1));
        _ticks.Should().Be(0);
        _time.Advance(TimeSpan.FromSeconds(1));
        _ticks.Should().Be(1);
        _time.Advance(Interval);
        _ticks.Should().Be(2);
    }

    [Fact]
    public void Skips_while_paused_or_hidden()
    {
        using var poller = Poller();
        poller.Start();

        poller.Paused = true;
        _time.Advance(Interval);
        poller.Paused = false;
        poller.Hidden = true;
        _time.Advance(Interval);
        _ticks.Should().Be(0);

        poller.Hidden = false;
        _time.Advance(Interval);
        _ticks.Should().Be(1);
    }

    [Fact]
    public void Never_overlaps_a_tick_that_is_still_running()
    {
        var pending = new TaskCompletionSource();
        using var poller = Poller(() => { _ticks++; return pending.Task; });
        poller.Start();

        _time.Advance(Interval);
        _time.Advance(Interval);
        _ticks.Should().Be(1);

        pending.SetResult();
        _time.Advance(Interval);
        _ticks.Should().Be(2);
    }

    [Fact]
    public void Swallows_tick_exceptions_and_keeps_polling()
    {
        using var poller = Poller(() => { _ticks++; throw new InvalidOperationException("boom"); });
        poller.Start();

        _time.Advance(Interval);
        _time.Advance(Interval);

        _ticks.Should().Be(2);
    }

    [Fact]
    public void Stop_and_dispose_end_the_ticks()
    {
        var poller = Poller();
        poller.Start();
        _time.Advance(Interval);
        poller.Stop();
        _time.Advance(Interval);
        _ticks.Should().Be(1);

        poller.Start();
        _time.Advance(Interval);
        _ticks.Should().Be(2);
        poller.Dispose();
        _time.Advance(Interval * 3);
        _ticks.Should().Be(2);
    }
}
