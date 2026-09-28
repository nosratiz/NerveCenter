using FluentAssertions;
using SbConsole.Plugins.Redis.Client;

namespace SbConsole.Plugins.Redis.Tests.Client;

public class RedisMemoryByPrefixSamplerTests
{
    [Fact]
    public void Groups_by_prefix_up_to_the_delimiter_and_sums_bytes()
    {
        SampledKey[] samples =
        [
            new("session:u:1", 1000, true),
            new("session:u:2", 2000, false),
            new("cart:u:1", 500, true),
        ];

        var result = RedisMemoryByPrefixSampler.Summarize(samples, delimiter: ':', topN: 4);

        result.Should().Contain(e => e.Prefix == "session:" && e.SampledBytes == 3000 && e.SampledKeyCount == 2 && e.KeysWithoutTtlCount == 1);
        result.Should().Contain(e => e.Prefix == "cart:" && e.SampledBytes == 500 && e.SampledKeyCount == 1 && e.KeysWithoutTtlCount == 0);
    }

    [Fact]
    public void Orders_prefixes_by_sampled_bytes_descending()
    {
        SampledKey[] samples =
        [
            new("small:1", 10, true),
            new("big:1", 900, true),
        ];

        var result = RedisMemoryByPrefixSampler.Summarize(samples, ':', topN: 4);

        result.Select(e => e.Prefix).Should().Equal("big:", "small:");
    }

    [Fact]
    public void Folds_prefixes_beyond_topN_into_an_other_bucket()
    {
        SampledKey[] samples =
        [
            new("a:1", 400, true), new("b:1", 300, true), new("c:1", 200, true), new("d:1", 100, true),
        ];

        var result = RedisMemoryByPrefixSampler.Summarize(samples, ':', topN: 2);

        result.Should().HaveCount(3);
        result.Should().Contain(e => e.Prefix == "a:" && e.SampledBytes == 400);
        result.Should().Contain(e => e.Prefix == "b:" && e.SampledBytes == 300);
        result.Should().Contain(e => e.Prefix == "other" && e.SampledBytes == 300 && e.SampledKeyCount == 2);
    }

    [Fact]
    public void A_key_with_no_delimiter_is_grouped_under_its_own_full_name()
    {
        SampledKey[] samples = [new("standalonekey", 42, true)];

        var result = RedisMemoryByPrefixSampler.Summarize(samples, ':', topN: 4);

        result.Should().ContainSingle().Which.Prefix.Should().Be("standalonekey");
    }
}
