using FluentAssertions;
using SbConsole.Plugins.RabbitMq.Components;

namespace SbConsole.Plugins.RabbitMq.Tests.Components;

public class RabbitFormatTests
{
    [Theory]
    [InlineData(0L, "0")]
    [InlineData(1840L, "1,840")]
    [InlineData(1234567L, "1,234,567")]
    public void Count_uses_invariant_grouping(long value, string expected) =>
        RabbitFormat.Count(value).Should().Be(expected);

    [Theory]
    [InlineData(1840.4, "1,840/s")]
    [InlineData(1839.6, "1,840/s")]
    [InlineData(0.2, "0.2/s")]
    [InlineData(9.94, "9.9/s")]
    [InlineData(37.0, "37/s")]
    [InlineData(0.0, "0/s")]
    public void Rate_formats_per_second(double value, string expected) =>
        RabbitFormat.Rate(value).Should().Be(expected);

    [Fact]
    public void Null_rate_is_an_em_dash_never_zero() => RabbitFormat.Rate(null).Should().Be("—");

    [Theory]
    [InlineData(412L, "412B")]
    [InlineData(18L * 1024 * 1024, "18M")]
    [InlineData(3113851290L, "2.9G")]
    [InlineData(180L * 1024 * 1024 * 1024, "180G")]
    [InlineData(1536L, "1.5K")]
    [InlineData(0L, "0B")]
    public void Bytes_are_1024_based_with_one_decimal_under_ten(long value, string expected) =>
        RabbitFormat.Bytes(value).Should().Be(expected);

    [Theory]
    [InlineData(4, "4s ago")]
    [InlineData(180, "3m ago")]
    [InlineData(7200, "2h ago")]
    [InlineData(14 * 86400, "14d ago")]
    [InlineData(-5, "0s ago")]
    public void Ago_picks_the_largest_whole_unit(int seconds, string expected)
    {
        var now = new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
        RabbitFormat.Ago(now.AddSeconds(-seconds), now).Should().Be(expected);
    }

    [Theory]
    [InlineData(14 * 86400 + 3600, "14d")]
    [InlineData(7200, "2h")]
    [InlineData(59, "59s")]
    public void Duration_is_compact(int seconds, string expected) =>
        RabbitFormat.Duration(TimeSpan.FromSeconds(seconds)).Should().Be(expected);

    [Theory]
    [InlineData("/", "/ (default)")]
    [InlineData("", "/ (default)")]
    [InlineData("/orders", "/orders")]
    public void VhostLabel_names_the_default_vhost(string vhost, string expected) =>
        RabbitFormat.VhostLabel(vhost).Should().Be(expected);
}
