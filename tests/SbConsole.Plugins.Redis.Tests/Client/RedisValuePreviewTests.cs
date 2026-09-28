using FluentAssertions;
using SbConsole.Plugins.Redis.Client;

namespace SbConsole.Plugins.Redis.Tests.Client;

public class RedisValuePreviewTests
{
    [Theory]
    [InlineData("{\"a\":1}", true)]
    [InlineData("[1,2,3]", true)]
    [InlineData("  {\"a\":1}  ", true)]
    [InlineData("hello world", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("42", false)]
    public void Looks_like_json_when_trimmed_value_starts_with_an_object_or_array_bracket(string value, bool expected)
    {
        RedisValuePreview.LooksLikeJson(value).Should().Be(expected);
    }
}
