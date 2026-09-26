using FluentAssertions;
using SbConsole.Plugins.RabbitMq.Components;

namespace SbConsole.Plugins.RabbitMq.Tests.Components;

public class HeaderLinesTests
{
    [Fact]
    public void Empty_or_blank_text_is_no_headers_and_no_errors()
    {
        HeaderLines.Parse("").Headers.Should().BeEmpty();
        HeaderLines.Parse(null).Errors.Should().BeEmpty();
        HeaderLines.Parse("  \n\n ").Headers.Should().BeEmpty();
    }

    [Fact]
    public void Parses_key_value_lines_trimming_and_splitting_on_the_first_colon()
    {
        var (headers, errors) = HeaderLines.Parse("x-source: sbconsole\r\n  region :uk \n\nurl: http://a:1/b");

        errors.Should().BeEmpty();
        headers.Should().Equal(new Dictionary<string, string>
        {
            ["x-source"] = "sbconsole",
            ["region"] = "uk",
            ["url"] = "http://a:1/b",
        });
    }

    [Fact]
    public void An_empty_value_is_allowed()
    {
        HeaderLines.Parse("flag:").Headers.Should().ContainKey("flag").WhoseValue.Should().BeEmpty();
    }

    [Fact]
    public void A_line_without_a_colon_or_with_an_empty_key_is_an_error_naming_the_line()
    {
        var (headers, errors) = HeaderLines.Parse("ok: 1\nnot a header\n: value");

        headers.Should().ContainSingle().Which.Key.Should().Be("ok");
        errors.Should().Equal(
            "Line 2: expected \"key: value\".",
            "Line 3: header name is empty.");
    }

    [Fact]
    public void A_duplicate_key_is_an_error()
    {
        var (_, errors) = HeaderLines.Parse("a: 1\na: 2");

        errors.Should().Equal("Line 2: header \"a\" is repeated.");
    }
}
