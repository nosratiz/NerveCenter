using FluentAssertions;
using StackExchange.Redis;
using SbConsole.Plugins.Redis.Client;

namespace SbConsole.Plugins.Redis.Tests.Client;

public class RedisSlowLogParserTests
{
    private static RedisResult Scalar(RedisValue v) => RedisResult.Create(v);
    private static RedisResult Arr(params RedisResult[] items) => RedisResult.Create(items);

    [Fact]
    public void Parses_id_timestamp_duration_command_and_client_address()
    {
        var reply = Arr(
            Arr(
                Scalar(14),                       // entry id (ignored — not part of SlowLogEntry)
                Scalar(1759050012),                // unix seconds
                Scalar(412000),                    // duration, microseconds
                Arr(Scalar("KEYS"), Scalar("session:*")),
                Scalar("10.4.8.31:51322"),
                Scalar("")));

        var entries = RedisSlowLogParser.Parse(reply);

        entries.Should().ContainSingle();
        var entry = entries[0];
        entry.At.ToUnixTimeSeconds().Should().Be(1759050012);
        entry.Duration.Should().Be(TimeSpan.FromMicroseconds(412000));
        entry.Command.Should().Be("KEYS session:*");
        entry.ClientAddress.Should().Be("10.4.8.31:51322");
    }

    [Fact]
    public void An_empty_reply_yields_no_entries()
    {
        RedisSlowLogParser.Parse(Arr()).Should().BeEmpty();
    }

    [Fact]
    public void Blank_client_name_field_does_not_affect_client_address()
    {
        var reply = Arr(Arr(Scalar(1), Scalar(1000), Scalar(500), Arr(Scalar("PING")), Scalar("127.0.0.1:1"), Scalar("my-client")));

        RedisSlowLogParser.Parse(reply)[0].ClientAddress.Should().Be("127.0.0.1:1");
    }
}
