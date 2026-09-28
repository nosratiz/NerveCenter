using FluentAssertions;
using SbConsole.Plugins.Redis.Components;

namespace SbConsole.Plugins.Redis.Tests.Components;

public class RedisJsonLayoutTests
{
    [Fact]
    public void Short_nested_objects_stay_on_one_line_inside_an_indented_block()
    {
        var formatted = RedisJsonLayout.Format("{\"currency\":\"GBP\",\"vatRate\":0.2,\"tiers\":[{\"min\":0,\"pct\":0},{\"min\":500,\"pct\":5}]}");

        formatted.Should().Be(
            "{\n" +
            "  \"currency\": \"GBP\",\n" +
            "  \"vatRate\": 0.2,\n" +
            "  \"tiers\": [\n" +
            "    { \"min\": 0, \"pct\": 0 },\n" +
            "    { \"min\": 500, \"pct\": 5 }\n" +
            "  ]\n" +
            "}");
    }

    [Fact]
    public void Tokens_mark_keys_strings_and_scalars()
    {
        var tokens = RedisJsonLayout.Tokenize("{\"a\":\"£1\",\"b\":true}")!;

        tokens.Should().Contain(new JsonToken("\"a\"", JsonTokenKind.Key));
        tokens.Should().Contain(new JsonToken("\"£1\"", JsonTokenKind.String));
        tokens.Should().Contain(new JsonToken("true", JsonTokenKind.Scalar));
    }

    [Fact]
    public void Invalid_json_returns_null()
    {
        RedisJsonLayout.Tokenize("{not json").Should().BeNull();
    }

    [Fact]
    public void Empty_containers_render_as_pairs()
    {
        RedisJsonLayout.Format("{\"a\":[],\"b\":{}}").Should().Be("{\n  \"a\": [],\n  \"b\": {}\n}");
    }
}
