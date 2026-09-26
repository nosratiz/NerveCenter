using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using SbConsole.Plugins.RabbitMq.Components;

namespace SbConsole.Plugins.RabbitMq.Tests.Components;

public class AutoRefreshTests
{
    private readonly FakeTimeProvider _time = new();
    private int _ticks;

    private Task Tick()
    {
        Interlocked.Increment(ref _ticks);
        return Task.CompletedTask;
    }

    private static Task Invoke(Func<Task> work) => work();

    private async Task AdvanceAsync(TimeSpan by)
    {
        _time.Advance(by);
        await Task.Delay(50);
    }

    [Fact]
    public async Task Ticks_once_per_interval_while_running()
    {
        using var refresh = new AutoRefresh(_time);
        refresh.Start(TimeSpan.FromSeconds(10), Tick, Invoke);

        refresh.IsRunning.Should().BeTrue();
        await AdvanceAsync(TimeSpan.FromSeconds(10));
        await AdvanceAsync(TimeSpan.FromSeconds(10));

        _ticks.Should().Be(2);
    }

    [Fact]
    public async Task Stop_cancels_the_loop()
    {
        using var refresh = new AutoRefresh(_time);
        refresh.Start(TimeSpan.FromSeconds(10), Tick, Invoke);

        refresh.Stop();
        await AdvanceAsync(TimeSpan.FromSeconds(30));

        refresh.IsRunning.Should().BeFalse();
        _ticks.Should().Be(0);
    }

    [Fact]
    public async Task Starting_twice_restarts_rather_than_running_two_loops()
    {
        using var refresh = new AutoRefresh(_time);
        refresh.Start(TimeSpan.FromSeconds(10), Tick, Invoke);
        refresh.Start(TimeSpan.FromSeconds(10), Tick, Invoke);

        await AdvanceAsync(TimeSpan.FromSeconds(10));

        _ticks.Should().Be(1);
    }

    [Fact]
    public async Task Start_after_dispose_is_a_no_op()
    {
        var refresh = new AutoRefresh(_time);
        refresh.Dispose();
        refresh.Start(TimeSpan.FromSeconds(10), Tick, Invoke);

        await AdvanceAsync(TimeSpan.FromSeconds(10));

        refresh.IsRunning.Should().BeFalse();
        _ticks.Should().Be(0);
    }

    [Fact]
    public async Task A_throwing_tick_is_reported_and_the_loop_continues()
    {
        var errors = new List<Exception>();
        using var refresh = new AutoRefresh(_time);
        refresh.Start(TimeSpan.FromSeconds(10), () => { Interlocked.Increment(ref _ticks); throw new InvalidOperationException("boom"); }, Invoke, errors.Add);

        await AdvanceAsync(TimeSpan.FromSeconds(10));
        await AdvanceAsync(TimeSpan.FromSeconds(10));

        _ticks.Should().Be(2);
        errors.Should().HaveCount(2);
    }
}
