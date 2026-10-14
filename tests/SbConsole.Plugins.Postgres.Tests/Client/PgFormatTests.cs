using FluentAssertions;
using SbConsole.Plugins.Postgres.Client;

namespace SbConsole.Plugins.Postgres.Tests.Client;

public class PgFormatTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(5L * 1024 * 1024 * 1024, "5 GB")]
    public void Bytes_formats(long v, string expected) => PgFormat.Bytes(v).Should().Be(expected);

    [Fact]
    public void Count_groups_thousands() => PgFormat.Count(1234567).Should().Be("1,234,567");

    [Theory]
    [InlineData(999L, "999")]
    [InlineData(88_000L, "88K")]
    [InlineData(2_100_000L, "2.1M")]
    [InlineData(410_000_000L, "410M")]
    [InlineData(1_100_000_000L, "1.1B")]
    public void Compact_abbreviates(long v, string expected) => PgFormat.Compact(v).Should().Be(expected);

    [Theory]
    [InlineData(842, "14m 02s")]
    [InlineData(41, "41 s")]
    [InlineData(0.8, "0.8 s")]
    [InlineData(9.4, "9.4 s")]
    [InlineData(11520, "3h 12m")]
    [InlineData(41 * 86400, "41d")]
    [InlineData(9.96, "10 s")]
    [InlineData(10, "10 s")]
    [InlineData(59.6, "1m 00s")]
    [InlineData(60, "1m 00s")]
    [InlineData(3600, "1h 00m")]
    [InlineData(86400, "1d")]
    [InlineData(-41, "41 s")]
    public void Duration_formats(double seconds, string expected) =>
        PgFormat.Duration(TimeSpan.FromSeconds(seconds)).Should().Be(expected);

    [Fact]
    public void Ago_boundary_at_one_second() => PgFormat.Ago(TimeSpan.FromSeconds(1)).Should().Be("1.0 s ago");

    [Fact]
    public void Compact_rounds_up_into_next_unit() => PgFormat.Compact(999_999).Should().Be("1M");

    [Fact]
    public void Ago_appends_ago_or_just_now()
    {
        PgFormat.Ago(TimeSpan.FromSeconds(12)).Should().Be("12 s ago");
        PgFormat.Ago(TimeSpan.FromMilliseconds(300)).Should().Be("just now");
    }

    [Fact]
    public void Percent_uses_decimals()
    {
        PgFormat.Percent(12.345).Should().Be("12.3%");
        PgFormat.Percent(12.345, 0).Should().Be("12%");
    }
}
