using FluentAssertions;
using SbConsole.Plugins.Postgres.Client;

namespace SbConsole.Plugins.Postgres.Tests.Client;

public class PgCellFormatterJsonTests
{
    [Theory]
    [InlineData("{}", "{…} 0 keys", 0)]
    [InlineData("{\"a\":1}", "{…} 1 key", 1)]
    [InlineData("{\"a\":1,\"b\":2}", "{…} 2 keys", 2)]
    [InlineData("[]", "[…] 0 items", 0)]
    [InlineData("[7]", "[…] 1 item", 1)]
    [InlineData("[1, [2, [3, [4]]]]", "[…] 2 items", 2)]
    public void Objects_and_arrays_collapse_to_a_labelled_count(string json, string display, int count)
    {
        var cell = PgCellFormatter.FormatJson(json);

        cell.Kind.Should().Be(PgCellKind.Json);
        cell.Display.Should().Be(display);
        cell.JsonKeyCount.Should().Be(count);
        cell.Full.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("\"hello\"", "\"hello\"")]
    [InlineData("42", "42")]
    [InlineData("1.50", "1.50")]
    [InlineData("true", "true")]
    [InlineData("null", "null")]
    public void Scalars_show_their_raw_json_text_as_a_text_cell(string json, string display)
    {
        var cell = PgCellFormatter.FormatJson(json);

        cell.Kind.Should().Be(PgCellKind.Text);
        cell.Display.Should().Be(display);
        cell.JsonKeyCount.Should().BeNull();
    }

    [Fact]
    public void A_long_scalar_string_is_truncated_with_the_full_text_kept()
    {
        var json = "\"" + new string('x', 300) + "\"";

        var cell = PgCellFormatter.FormatJson(json);

        cell.Kind.Should().Be(PgCellKind.Text);
        cell.Display!.Length.Should().Be(PgCellFormatter.DisplayChars + 1);
        cell.Full.Should().Be(json);
    }

    [Fact]
    public void Unparseable_text_stays_a_text_cell()
    {
        PgCellFormatter.FormatJson("{nope").Kind.Should().Be(PgCellKind.Text);
    }
}
