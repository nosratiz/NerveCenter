using FluentAssertions;

namespace SbConsole.Plugins.Aws.Tests.Pages;

// Receive.razor's hold banner needs a live countdown, but bUnit can't fast-forward a real
// PeriodicTimer through wall-clock time -- so the actual remaining-seconds math is pulled out as a
// pure static function (Receive.SecondsRemaining) and tested directly here, per TDD's usual "test
// the pure calculation, not the timer" split for this codebase.
public class ReceiveCountdownTests
{
    [Fact]
    public void SecondsRemaining_rounds_up_a_partial_second_to_the_next_whole_second()
    {
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddSeconds(29.4);

        SbConsole.Plugins.Aws.Pages.Receive.SecondsRemaining(expiresAt, now).Should().Be(30);
    }

    [Fact]
    public void SecondsRemaining_is_zero_exactly_at_expiry()
    {
        var now = DateTimeOffset.UtcNow;

        SbConsole.Plugins.Aws.Pages.Receive.SecondsRemaining(now, now).Should().Be(0);
    }

    [Fact]
    public void SecondsRemaining_clamps_to_zero_once_expiry_is_in_the_past()
    {
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddSeconds(-5);

        SbConsole.Plugins.Aws.Pages.Receive.SecondsRemaining(expiresAt, now).Should().Be(0);
    }
}
