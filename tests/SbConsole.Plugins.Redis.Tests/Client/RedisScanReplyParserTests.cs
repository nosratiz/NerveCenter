using FluentAssertions;
using StackExchange.Redis;
using SbConsole.Plugins.Redis.Client;

namespace SbConsole.Plugins.Redis.Tests.Client;

public class RedisScanReplyParserTests
{
    private static RedisResult Scalar(RedisValue v) => RedisResult.Create(v);
    private static RedisResult Arr(params RedisResult[] items) => RedisResult.Create(items);

    [Fact]
    public void Parses_cursor_and_matched_items_from_a_two_element_multi_bulk_reply()
    {
        var reply = Arr(Scalar("42"), Arr(Scalar("session:1"), Scalar("session:2")));

        var (cursor, items) = RedisScanReplyParser.Parse(reply);

        cursor.Should().Be(42);
        items.Should().Equal("session:1", "session:2");
    }

    [Fact]
    public void A_cursor_of_zero_means_the_scan_is_complete()
    {
        var reply = Arr(Scalar("0"), Arr());

        RedisScanReplyParser.Parse(reply).Cursor.Should().Be(0);
    }

    [Fact]
    public void An_empty_items_array_yields_no_items()
    {
        var reply = Arr(Scalar("100"), Arr());

        RedisScanReplyParser.Parse(reply).Items.Should().BeEmpty();
    }

    [Fact]
    public void Works_for_a_flat_field_value_reply_such_as_HSCAN()
    {
        // HSCAN/ZSCAN flatten field/value or member/score pairs into the same items array shape;
        // the parser itself is agnostic to pairing -- callers interpret pairs.
        var reply = Arr(Scalar("0"), Arr(Scalar("csrf"), Scalar("abc123"), Scalar("expires"), Scalar("1700000000")));

        var (cursor, items) = RedisScanReplyParser.Parse(reply);

        cursor.Should().Be(0);
        items.Should().Equal("csrf", "abc123", "expires", "1700000000");
    }
}
