using FluentAssertions;
using SbConsole.Plugins.Redis.Components;

namespace SbConsole.Plugins.Redis.Tests.Components;

public class RedisFormatTests
{
    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(64L, "64 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(4096L, "4 KB")]
    [InlineData(38_912L, "38 KB")]
    [InlineData(1_992_294L, "1.9 MB")]
    [InlineData(7_405_032_140L, "6.9 GB")]
    [InlineData(4_294_967_296L, "4.0 GB")]
    public void Bytes_uses_binary_scaling_with_whole_B_and_KB(long bytes, string expected) =>
        RedisFormat.Bytes(bytes).Should().Be(expected);

    [Theory]
    [InlineData(0L, "0")]
    [InlineData(142L, "142")]
    [InlineData(9_999L, "9,999")]
    [InlineData(18_200L, "18.2k")]
    [InlineData(41_800L, "41.8k")]
    [InlineData(2_410_000L, "2.41M")]
    [InlineData(184_210L, "184k")]
    public void Compact_keeps_three_significant_digits(long value, string expected) =>
        RedisFormat.Compact(value).Should().Be(expected);

    [Fact]
    public void Count_uses_invariant_thousands_separators() =>
        RedisFormat.Count(184_210).Should().Be("184,210");

    [Theory]
    [InlineData(400, "0.4s")]
    [InlineData(1_200, "1.2s")]
    [InlineData(3_000, "3s")]
    [InlineData(42_000, "42s")]
    [InlineData(29 * 60_000, "29m")]
    [InlineData((2 * 60 + 4) * 60_000, "2h 4m")]
    [InlineData(3 * 3_600_000, "3h")]
    [InlineData((57 * 24 + 5) * 3_600_000L, "57d 5h")]
    public void Duration_is_short_and_human(long milliseconds, string expected) =>
        RedisFormat.Duration(TimeSpan.FromMilliseconds(milliseconds)).Should().Be(expected);

    [Fact]
    public void StreamIdTime_reads_the_millisecond_prefix_of_a_stream_id() =>
        RedisFormat.StreamIdTime("1759052468112-0").Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1759052468112));

    [Theory]
    [InlineData("not-an-id")]
    [InlineData("")]
    public void StreamIdTime_is_null_for_anything_else(string id) =>
        RedisFormat.StreamIdTime(id).Should().BeNull();
}
