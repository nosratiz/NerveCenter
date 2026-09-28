using FluentAssertions;
using StackExchange.Redis;
using SbConsole.Plugins.Redis.Client;

namespace SbConsole.Plugins.Redis.Tests.Client;

public class RedisKeyTypeNamesTests
{
    [Theory]
    [InlineData(RedisType.String, RedisKeyType.String)]
    [InlineData(RedisType.List, RedisKeyType.List)]
    [InlineData(RedisType.Set, RedisKeyType.Set)]
    [InlineData(RedisType.SortedSet, RedisKeyType.SortedSet)]
    [InlineData(RedisType.Hash, RedisKeyType.Hash)]
    [InlineData(RedisType.Stream, RedisKeyType.Stream)]
    [InlineData(RedisType.None, RedisKeyType.Unknown)]
    [InlineData(RedisType.Unknown, RedisKeyType.Unknown)]
    public void Maps_every_StackExchange_RedisType_to_our_RedisKeyType(RedisType input, RedisKeyType expected)
    {
        RedisKeyTypeNames.FromRedisType(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(RedisKeyType.String, "string")]
    [InlineData(RedisKeyType.List, "list")]
    [InlineData(RedisKeyType.Set, "set")]
    [InlineData(RedisKeyType.SortedSet, "zset")]
    [InlineData(RedisKeyType.Hash, "hash")]
    [InlineData(RedisKeyType.Stream, "stream")]
    public void Maps_every_filterable_type_to_its_SCAN_TYPE_token(RedisKeyType input, string expectedToken)
    {
        RedisKeyTypeNames.ToScanToken(input).Should().Be(expectedToken);
    }

    [Fact]
    public void Unknown_has_no_SCAN_TYPE_token_because_it_is_not_a_real_redis_type()
    {
        RedisKeyTypeNames.ToScanToken(RedisKeyType.Unknown).Should().BeNull();
    }
}
