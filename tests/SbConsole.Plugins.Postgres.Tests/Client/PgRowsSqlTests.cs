using FluentAssertions;
using SbConsole.Plugins.Postgres.Client;

namespace SbConsole.Plugins.Postgres.Tests.Client;

public class PgRowsSqlTests
{
    private const string Q = "\"public\".\"orders\"";

    private static readonly IReadOnlyList<(string Name, string Type)> Columns =
    [
        ("id", "bigint"), ("status", "text"), ("metadata", "jsonb"), ("tags", "text[]"),
        ("created_at", "timestamp with time zone"), ("we\"ird", "text"),
    ];

    private static PgRowsRequest Req(params PgFilter[] filters) =>
        new(1, filters, null, false, 0, 50);

    [Fact]
    public void No_filters_no_sort_selects_with_limit_and_offset()
    {
        var (sql, p) = PgRowsSql.Build(Q, Columns, Req() with { Offset = 100, Limit = 25 });
        sql.Should().Be($"SELECT * FROM {Q} LIMIT 25 OFFSET 100");
        p.Should().BeEmpty();
    }

    [Theory]
    [InlineData(PgFilterOp.Eq, "=")]
    [InlineData(PgFilterOp.NotEq, "<>")]
    [InlineData(PgFilterOp.Gt, ">")]
    [InlineData(PgFilterOp.Gte, ">=")]
    [InlineData(PgFilterOp.Lt, "<")]
    [InlineData(PgFilterOp.Lte, "<=")]
    public void Comparison_operators_use_a_typed_parameter(PgFilterOp op, string symbol)
    {
        var (sql, p) = PgRowsSql.Build(Q, Columns, Req(new PgFilter("id", op, "5")));
        sql.Should().Be($"SELECT * FROM {Q} WHERE \"id\" {symbol} $1::bigint LIMIT 50 OFFSET 0");
        p.Should().Equal("5");
    }

    [Fact]
    public void Type_cast_uses_the_catalog_type_text()
    {
        var (sql, _) = PgRowsSql.Build(Q, Columns, Req(new PgFilter("created_at", PgFilterOp.Gt, "2024-01-01")));
        sql.Should().Contain("\"created_at\" > $1::timestamp with time zone");
    }

    [Fact]
    public void Like_and_ilike_compare_the_column_as_text()
    {
        var (sql, p) = PgRowsSql.Build(Q, Columns, Req(
            new PgFilter("id", PgFilterOp.Like, "1%"), new PgFilter("status", PgFilterOp.ILike, "%PEN%")));
        sql.Should().Contain("WHERE \"id\"::text LIKE $1::text AND \"status\"::text ILIKE $2::text");
        p.Should().Equal("1%", "%PEN%");
    }

    [Fact]
    public void Null_operators_take_no_parameter()
    {
        var (sql, p) = PgRowsSql.Build(Q, Columns, Req(
            new PgFilter("status", PgFilterOp.IsNull, null), new PgFilter("id", PgFilterOp.IsNotNull, "ignored")));
        sql.Should().Contain("WHERE \"status\" IS NULL AND \"id\" IS NOT NULL");
        p.Should().BeEmpty();
    }

    [Fact]
    public void Contains_works_on_jsonb_and_arrays()
    {
        var (sql, p) = PgRowsSql.Build(Q, Columns, Req(
            new PgFilter("metadata", PgFilterOp.Contains, "{\"a\":1}"), new PgFilter("tags", PgFilterOp.Contains, "{x}")));
        sql.Should().Contain("WHERE \"metadata\" @> $1::jsonb AND \"tags\" @> $2::text[]");
        p.Should().Equal("{\"a\":1}", "{x}");
    }

    [Fact]
    public void Contains_on_a_text_column_is_refused() =>
        FluentActions.Invoking(() => PgRowsSql.Build(Q, Columns, Req(new PgFilter("status", PgFilterOp.Contains, "x"))))
            .Should().Throw<PgSqlRefusedException>();

    [Fact]
    public void Unknown_column_is_refused() =>
        FluentActions.Invoking(() => PgRowsSql.Build(Q, Columns, Req(new PgFilter("nope; drop table x", PgFilterOp.Eq, "1"))))
            .Should().Throw<PgSqlRefusedException>();

    [Fact]
    public void Unknown_sort_column_is_refused() =>
        FluentActions.Invoking(() => PgRowsSql.Build(Q, Columns, Req() with { SortColumn = "nope" }))
            .Should().Throw<PgSqlRefusedException>();

    [Fact]
    public void Value_operator_without_a_value_is_refused() =>
        FluentActions.Invoking(() => PgRowsSql.Build(Q, Columns, Req(new PgFilter("id", PgFilterOp.Eq, null))))
            .Should().Throw<PgSqlRefusedException>();

    [Fact]
    public void Identifier_with_a_quote_is_quoted_and_the_value_stays_a_parameter()
    {
        var (sql, p) = PgRowsSql.Build(Q, Columns, Req(new PgFilter("we\"ird", PgFilterOp.Eq, "'; DROP TABLE x; --")));
        sql.Should().Contain("\"we\"\"ird\" = $1::text");
        sql.Should().NotContain("DROP");
        p.Should().Equal("'; DROP TABLE x; --");
    }

    [Theory]
    [InlineData(false, "ASC")]
    [InlineData(true, "DESC")]
    public void Sort_orders_with_nulls_last(bool desc, string dir)
    {
        var (sql, _) = PgRowsSql.Build(Q, Columns, Req() with { SortColumn = "id", SortDescending = desc });
        sql.Should().Be($"SELECT * FROM {Q} ORDER BY \"id\" {dir} NULLS LAST LIMIT 50 OFFSET 0");
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1000, 100)]
    public void Limit_is_clamped(int limit, int expected)
    {
        var (sql, _) = PgRowsSql.Build(Q, Columns, Req() with { Limit = limit });
        sql.Should().EndWith($"LIMIT {expected} OFFSET 0");
    }

    [Fact]
    public void Negative_offset_is_clamped_to_zero()
    {
        var (sql, _) = PgRowsSql.Build(Q, Columns, Req() with { Offset = -9 });
        sql.Should().EndWith("OFFSET 0");
    }

    [Fact]
    public void For_count_drops_order_limit_and_offset()
    {
        var (sql, _) = PgRowsSql.Build(Q, Columns, Req(new PgFilter("id", PgFilterOp.Eq, "1")) with { SortColumn = "id" }, forCount: true);
        sql.Should().Be($"SELECT * FROM {Q} WHERE \"id\" = $1::bigint");
    }

    [Fact]
    public void Export_keeps_order_but_drops_limit_and_offset()
    {
        var (sql, _) = PgRowsSql.BuildExport(Q, Columns, Req() with { SortColumn = "id", SortDescending = true });
        sql.Should().Be($"SELECT * FROM {Q} ORDER BY \"id\" DESC NULLS LAST");
    }
}
