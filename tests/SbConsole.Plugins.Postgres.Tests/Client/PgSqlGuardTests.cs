using FluentAssertions;
using SbConsole.Plugins.Postgres.Client;

namespace SbConsole.Plugins.Postgres.Tests.Client;

public class PgSqlGuardTests
{
    [Fact]
    public void Allows_single_select_in_read_mode() =>
        PgSqlGuard.Check("select 1;", write: false).Should().HaveCount(1);

    [Fact]
    public void Allows_multi_statement_write() =>
        PgSqlGuard.Check("update t set a=1; delete from t", write: true).Should().HaveCount(2);

    [Fact]
    public void Refuses_multiple_statements_in_read_mode() =>
        FluentActions.Invoking(() => PgSqlGuard.Check("select 1; select 2", false))
            .Should().Throw<PgSqlRefusedException>()
            .WithMessage("Read-only mode runs one statement at a time — this has 2. Switch to Write to run a script.");

    [Theory]
    [InlineData("commit", false)]
    [InlineData("begin", true)]
    [InlineData("set x = 1", false)]
    public void Refuses_transaction_control_with_keyword(string sql, bool write)
    {
        var kw = sql.Split(' ')[0].ToUpperInvariant();
        FluentActions.Invoking(() => PgSqlGuard.Check(sql, write))
            .Should().Throw<PgSqlRefusedException>()
            .WithMessage($"`{kw}` can't run here — the console manages the transaction.");
    }

    [Fact]
    public void Allows_set_in_write_mode() =>
        PgSqlGuard.Check("set local x = 1", write: true).Should().HaveCount(1);

    [Fact]
    public void Refuses_vacuum_in_write_mode() =>
        FluentActions.Invoking(() => PgSqlGuard.Check("VACUUM t", true))
            .Should().Throw<PgSqlRefusedException>()
            .WithMessage("VACUUM can't run in a transaction — use Maintenance.");

    [Fact]
    public void Refuses_vacuum_in_read_mode() =>
        FluentActions.Invoking(() => PgSqlGuard.Check("VACUUM t", false))
            .Should().Throw<PgSqlRefusedException>()
            .WithMessage("VACUUM can't run in a transaction — use Maintenance.");

    [Fact]
    public void Refuses_empty_input() =>
        FluentActions.Invoking(() => PgSqlGuard.Check("  -- nothing\n ;", false))
            .Should().Throw<PgSqlRefusedException>().WithMessage("Nothing to run.");

    [Theory]
    [InlineData("create index concurrently i on t(a)")]
    [InlineData("REINDEX INDEX CONCURRENTLY i")]
    [InlineData("create unique index  concurrently i on t(a)")]
    [InlineData("create database x")]
    [InlineData("drop database x")]
    [InlineData("alter system set work_mem = '1MB'")]
    public void Refuses_statements_that_cannot_run_in_a_transaction_in_write_mode(string sql) =>
        FluentActions.Invoking(() => PgSqlGuard.Check(sql, true))
            .Should().Throw<PgSqlRefusedException>();

    [Fact]
    public void Refuses_create_index_concurrently_in_write_mode() =>
        FluentActions.Invoking(() => PgSqlGuard.Check("CREATE INDEX CONCURRENTLY i ON t(a)", true))
            .Should().Throw<PgSqlRefusedException>()
            .WithMessage("`CREATE INDEX CONCURRENTLY` can't run in a transaction — run it from a SQL client.");

    [Theory]
    [InlineData("SELECT 1 -- x\r; COMMIT; DELETE FROM t")]
    [InlineData("SELECT 1 AS é$$; COMMIT; DELETE FROM t; SELECT $$x")]
    [InlineData("SELECT éE'\\'; COMMIT; DELETE FROM t; --'")]
    public void Read_mode_refuses_lexer_bypass_inputs(string sql) =>
        FluentActions.Invoking(() => PgSqlGuard.Check(sql, false))
            .Should().Throw<PgSqlRefusedException>();
}
