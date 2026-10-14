using FluentAssertions;
using SbConsole.Plugins.Postgres.Client;

namespace SbConsole.Plugins.Postgres.Tests.Client;

public class PgSqlLexerTests
{
    private static string[] Texts(string sql) => PgSqlLexer.Split(sql).Select(s => s.Text).ToArray();

    [Fact]
    public void Splits_on_top_level_semicolons() =>
        Texts("select 1; select 2;").Should().Equal("select 1", "select 2");

    [Fact]
    public void Ignores_semicolons_in_single_quotes_with_doubled_quotes() =>
        Texts("select 'a'';b'; select 2").Should().Equal("select 'a'';b'", "select 2");

    [Fact]
    public void Ignores_semicolons_in_E_strings_with_backslash_escapes() =>
        Texts(@"select E'a\';b'; select 2").Should().Equal(@"select E'a\';b'", "select 2");

    [Fact]
    public void Ignores_semicolons_in_dollar_quotes_with_tags() =>
        Texts("do $fn$ begin x; end $fn$; select 1").Should().Equal("do $fn$ begin x; end $fn$", "select 1");

    [Fact]
    public void A_parameter_like_dollar_one_is_not_a_dollar_quote() =>
        Texts("select $1; select $2").Should().Equal("select $1", "select $2");

    [Fact]
    public void Ignores_semicolons_in_line_and_nested_block_comments() =>
        Texts("select 1 -- a;b\n; select /* x; /* y; */ z; */ 2")
            .Should().Equal("select 1 -- a;b", "select /* x; /* y; */ z; */ 2");

    [Fact]
    public void Ignores_semicolons_in_quoted_identifiers() =>
        Texts("select \"a\"\";b\"; select 2").Should().Equal("select \"a\"\";b\"", "select 2");

    [Fact]
    public void Drops_empty_statements() =>
        Texts(" ; ;select 1;; ").Should().Equal("select 1");

    [Fact]
    public void Unterminated_quote_runs_to_the_end() =>
        Texts("select 'abc; select 2").Should().Equal("select 'abc; select 2");

    [Fact]
    public void First_keyword_skips_leading_comments()
    {
        var s = PgSqlLexer.Split("-- hi\n /* x */ with a as (select 1) select * from a");
        s.Single().FirstKeyword.Should().Be("WITH");
    }

    [Fact]
    public void Statement_start_and_length_point_into_the_source()
    {
        var sql = "select 1;  select 2";
        var s = PgSqlLexer.Split(sql)[1];
        sql.Substring(s.Start, s.Length).Should().Be(s.Text);
    }

    [Fact]
    public void Line_and_column_from_position()
    {
        var sql = "select 1\nfrom x";
        PgSqlLexer.LineAndColumn(sql, 1).Should().Be((1, 1));
        PgSqlLexer.LineAndColumn(sql, 10).Should().Be((2, 1));
        PgSqlLexer.LineAndColumn(sql, 12).Should().Be((2, 3));
        PgSqlLexer.LineAndColumn(sql, 999).Should().Be((2, 7));
        PgSqlLexer.LineAndColumn(sql, 0).Should().Be((1, 1));
    }

    [Fact]
    public void Line_comment_ends_at_carriage_return() =>
        PgSqlLexer.Split("SELECT 1 -- x\r; COMMIT; DELETE FROM t").Should().HaveCount(3);

    [Fact]
    public void Non_ascii_identifier_char_prevents_dollar_quote() =>
        PgSqlLexer.Split("SELECT 1 AS é$$; COMMIT; DELETE FROM t; SELECT $$x").Should().HaveCount(4);

    [Fact]
    public void Non_ascii_identifier_char_prevents_escape_string() =>
        PgSqlLexer.Split("SELECT éE'\\'; COMMIT; DELETE FROM t; --'").Should().HaveCountGreaterThanOrEqualTo(2);

    [Fact]
    public void Dollar_quote_tag_may_start_with_non_ascii_letter() =>
        PgSqlLexer.Split("$é$ a;b $é$").Should().HaveCount(1);
}
