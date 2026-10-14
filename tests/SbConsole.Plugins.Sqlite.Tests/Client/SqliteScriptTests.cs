using FluentAssertions;
using SbConsole.Plugins.Sqlite.Client;

namespace SbConsole.Plugins.Sqlite.Tests.Client;

/// <summary>Design spec §11.2: splitting user scripts into statements, and the refusals.</summary>
public class SqliteScriptTests
{
    private static IReadOnlyList<string> Texts(string sql) => SqliteScript.Split(sql).Select(s => s.Text).ToList();

    [Fact]
    public void Splits_at_semicolons_and_drops_the_terminator()
    {
        Texts("SELECT 1; SELECT 2;").Should().Equal("SELECT 1", "SELECT 2");
    }

    [Fact]
    public void Keeps_a_trailing_statement_without_a_semicolon()
    {
        Texts("SELECT 1;\nSELECT 2").Should().Equal("SELECT 1", "SELECT 2");
    }

    [Fact]
    public void Drops_whitespace_only_and_empty_pieces()
    {
        Texts(" ;;\n\t; SELECT 1 ;  ; \n").Should().Equal("SELECT 1");
    }

    [Fact]
    public void Drops_a_comment_only_tail()
    {
        Texts("SELECT 1; -- done\n/* really */").Should().Equal("SELECT 1");
    }

    [Theory]
    [InlineData("SELECT 'a;b'; SELECT 2", "SELECT 'a;b'")]
    [InlineData("SELECT 'it''s; fine'; SELECT 2", "SELECT 'it''s; fine'")]
    [InlineData("SELECT \"a;b\" FROM t; SELECT 2", "SELECT \"a;b\" FROM t")]
    [InlineData("SELECT `a;b` FROM t; SELECT 2", "SELECT `a;b` FROM t")]
    [InlineData("SELECT [a;b] FROM t; SELECT 2", "SELECT [a;b] FROM t")]
    [InlineData("SELECT 1 -- not; here\n; SELECT 2", "SELECT 1 -- not; here")]
    [InlineData("SELECT /* not; here */ 1; SELECT 2", "SELECT /* not; here */ 1")]
    public void Ignores_semicolons_inside_quotes_and_comments(string sql, string first)
    {
        Texts(sql).Should().Equal(first, "SELECT 2");
    }

    [Fact]
    public void Keeps_a_trigger_body_in_one_statement()
    {
        const string trigger = "CREATE TRIGGER t AFTER INSERT ON a BEGIN INSERT INTO b VALUES (1); UPDATE b SET x = 2; END";

        var statements = SqliteScript.Split(trigger + "; SELECT 1");

        statements.Select(s => s.Text).Should().Equal(trigger, "SELECT 1");
        statements[0].Keyword.Should().Be("CREATE");
    }

    [Fact]
    public void A_quote_with_a_bracket_or_comment_marker_does_not_confuse_the_tokenizer()
    {
        Texts("SELECT '[', '--', '/*'; SELECT 2").Should().Equal("SELECT '[', '--', '/*'", "SELECT 2");
    }

    [Fact]
    public void Detects_the_leading_keyword_after_comments_in_upper_case()
    {
        var statements = SqliteScript.Split("-- tidy up\n/* old rows */ delete from a; \n  select 1");

        statements.Select(s => s.Keyword).Should().Equal("DELETE", "SELECT");
    }

    [Fact]
    public void A_statement_that_does_not_start_with_a_word_has_an_empty_keyword()
    {
        SqliteScript.Split("(SELECT 1)").Single().Keyword.Should().BeEmpty();
    }

    [Theory]
    [InlineData("EXPLAIN SELECT 1", "SELECT", "SELECT 1")]
    [InlineData("explain query plan SELECT * FROM a", "SELECT", "SELECT * FROM a")]
    [InlineData("EXPLAIN /* x */ QUERY -- y\n PLAN DELETE FROM a", "DELETE", "DELETE FROM a")]
    public void Explain_prefixed_statements_report_the_explained_keyword_and_body(string sql, string keyword, string body)
    {
        var statement = SqliteScript.Split(sql).Single();

        statement.IsExplain.Should().BeTrue();
        statement.Keyword.Should().Be(keyword);
        statement.Body.Should().Be(body);
        statement.Text.Should().Be(sql);
    }

    [Fact]
    public void A_plain_statement_has_its_text_as_body()
    {
        var statement = SqliteScript.Split("  SELECT 1  ").Single();

        statement.IsExplain.Should().BeFalse();
        statement.Body.Should().Be("SELECT 1");
    }

    [Fact]
    public void A_column_named_like_a_keyword_in_quotes_is_not_the_keyword()
    {
        SqliteScript.Split("\"attach\"").Single().Keyword.Should().BeEmpty();
    }

    [Fact]
    public void Parse_returns_the_statements_of_an_allowed_script()
    {
        SqliteScript.Parse("SELECT 1; UPDATE a SET x = 1").Statements.Should().HaveCount(2);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n ")]
    [InlineData(" ; ; ")]
    [InlineData("-- nothing to see")]
    public void Parse_refuses_an_empty_script(string sql)
    {
        var act = () => SqliteScript.Parse(sql);

        act.Should().Throw<SqliteScriptRefusedException>().WithMessage("The script is empty — type at least one statement.");
    }

    [Theory]
    [InlineData("ATTACH 'x.db' AS x", "ATTACH")]
    [InlineData("SELECT 1; detach x", "DETACH")]
    public void Parse_refuses_attach_and_detach(string sql, string keyword)
    {
        var act = () => SqliteScript.Parse(sql);

        act.Should().Throw<SqliteScriptRefusedException>()
            .WithMessage($"{keyword} is not allowed (statement *) — the console only opens the connection's own file.");
    }

    [Theory]
    [InlineData("VACUUM")]
    [InlineData("VACUUM INTO '/tmp/copy.db'")]
    [InlineData("SELECT 1; /* sneaky */ vacuum")]
    public void Parse_refuses_vacuum(string sql)
    {
        var act = () => SqliteScript.Parse(sql);

        act.Should().Throw<SqliteScriptRefusedException>().WithMessage("VACUUM is not allowed (statement *) — use Backups for a compacted copy.");
    }

    [Theory]
    [InlineData("BEGIN; DELETE FROM a; COMMIT", "BEGIN")]
    [InlineData("DELETE FROM a; COMMIT", "COMMIT")]
    [InlineData("DELETE FROM a; END", "END")]
    [InlineData("DELETE FROM a; ROLLBACK", "ROLLBACK")]
    [InlineData("SAVEPOINT s; DELETE FROM a", "SAVEPOINT")]
    [InlineData("RELEASE s", "RELEASE")]
    public void Parse_refuses_transaction_control(string sql, string keyword)
    {
        var act = () => SqliteScript.Parse(sql);

        act.Should().Throw<SqliteScriptRefusedException>()
            .WithMessage($"{keyword} is not allowed (statement *) — the console runs every script in its own transaction.");
    }

    [Fact]
    public void Parse_names_the_offending_statement_number()
    {
        var act = () => SqliteScript.Parse("SELECT 1; SELECT 2; ATTACH 'x' AS y");

        act.Should().Throw<SqliteScriptRefusedException>().WithMessage("ATTACH is not allowed (statement 3)*");
    }

    [Fact]
    public void Parse_refuses_an_explained_refused_statement_too()
    {
        var act = () => SqliteScript.Parse("EXPLAIN VACUUM");

        act.Should().Throw<SqliteScriptRefusedException>();
    }

    [Fact]
    public void A_trigger_body_with_begin_and_end_is_not_transaction_control()
    {
        var act = () => SqliteScript.Parse("CREATE TRIGGER t AFTER INSERT ON a BEGIN DELETE FROM b; END;");

        act.Should().NotThrow();
    }
}
