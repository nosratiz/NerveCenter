using FluentAssertions;
using SbConsole.Plugins.Sqlite.Client;

namespace SbConsole.Plugins.Sqlite.Tests.Client;

public class SqliteFormatTests
{
    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(512L, "512 B")]
    [InlineData(4096L, "4 KB")]
    [InlineData(65_536L, "64 KB")]
    [InlineData(50_331_648L, "48 MB")]
    [InlineData(1_395_864_371L, "1.3 GB")]
    [InlineData(2_254_857_830L, "2.1 GB")]
    public void Bytes_uses_binary_units_with_one_decimal_below_ten(long bytes, string expected) =>
        SqliteFormat.Bytes(bytes).Should().Be(expected);

    [Theory]
    [InlineData(412L, "412")]
    [InlineData(2_418_902L, "2.4M")]
    [InlineData(41_200_000L, "41.2M")]
    [InlineData(999_999L, "999,999")]
    public void Approx_compacts_millions(long rows, string expected) =>
        SqliteFormat.Approx(rows).Should().Be(expected);

    [Fact]
    public void Count_groups_thousands() => SqliteFormat.Count(2_418_902).Should().Be("2,418,902");

    [Theory]
    [InlineData(412, "412 ms")]
    [InlineData(41_000, "41 s")]
    [InlineData(2_500, "2.5 s")]
    [InlineData(134_000, "2m 14s")]
    public void Duration_reads_like_the_mockup(int milliseconds, string expected) =>
        SqliteFormat.Duration(TimeSpan.FromMilliseconds(milliseconds)).Should().Be(expected);
}
