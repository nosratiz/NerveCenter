using System.Text;
using FluentAssertions;
using SbConsole.Plugins.Postgres.Client;
using SbConsole.Plugins.Postgres.Tests.Infrastructure;

namespace SbConsole.Plugins.Postgres.Tests.Client;

[Collection("postgres")]
public sealed class OperationsRowsTests(PostgresFixture fx)
{
    private async Task<uint> OidAsync(string name)
    {
        var tree = await fx.Operations.ListSchemaAsync(fx.Secret(), null);
        return tree.Schemas.Single(s => s.Name == "public").Tables.Single(t => t.Name == name).Oid;
    }

    private static PgRowsRequest Req(uint oid, params PgFilter[] filters) => new(oid, filters, "id", false, 0, 50);

    private static int Col(PgRowPage page, string name) => page.Columns.Select(c => c.Name).ToList().IndexOf(name);

    [DockerFact]
    public async Task Preview_returns_a_page_with_columns_and_types()
    {
        var page = await fx.Operations.PreviewRowsAsync(fx.Secret(), null, Req(await OidAsync("orders")));

        page.Rows.Should().HaveCount(50);
        page.Columns.Select(c => c.Name).Should().Equal("id", "customer_id", "status", "total_cents", "metadata", "created_at");
        page.Columns[0].TypeName.Should().Be("bigint");
        page.Columns[4].TypeName.Should().Be("jsonb");
        page.Rows[0][0].Display.Should().Be("1");
        page.EstimatedMatching.Should().BeGreaterThan(1000);
    }

    [DockerFact]
    public async Task Preview_honours_offset_and_descending_sort()
    {
        var oid = await OidAsync("orders");
        var page = await fx.Operations.PreviewRowsAsync(fx.Secret(), null,
            new PgRowsRequest(oid, [], "id", true, 10, 5));

        page.Rows.Select(r => r[0].Display).Should().Equal("1990", "1989", "1988", "1987", "1986");
    }

    [DockerFact]
    public async Task Eq_filter_returns_matching_rows_and_a_filtered_estimate()
    {
        var page = await fx.Operations.PreviewRowsAsync(fx.Secret(), null,
            Req(await OidAsync("orders"), new PgFilter("status", PgFilterOp.Eq, "pending")) with { Limit = 100 });

        page.Rows.Should().HaveCount(100);
        page.Rows.Select(r => r[2].Display).Should().OnlyContain(s => s == "pending");
        page.EstimatedMatching.Should().BeGreaterThan(0).And.BeLessThan(2000);
    }

    [DockerFact]
    public async Task Typed_numeric_filter_casts_the_parameter()
    {
        var oid = await OidAsync("orders");
        var page = await fx.Operations.PreviewRowsAsync(fx.Secret(), null,
            Req(oid, new PgFilter("id", PgFilterOp.Gte, "1999")));
        page.Rows.Select(r => r[0].Display).Should().Equal("1999", "2000");
    }

    [DockerFact]
    public async Task Jsonb_contains_filter_works()
    {
        var page = await fx.Operations.PreviewRowsAsync(fx.Secret(), null,
            Req(await OidAsync("orders"), new PgFilter("metadata", PgFilterOp.Contains, "{\"n\":5}")));

        page.Rows.Should().ContainSingle();
        page.Rows[0][0].Display.Should().Be("5");
    }

    [DockerFact]
    public async Task Array_contains_and_like_and_null_filters_work()
    {
        var oid = await OidAsync("samples");
        (await fx.Operations.PreviewRowsAsync(fx.Secret(), null, Req(oid, new PgFilter("tags", PgFilterOp.Contains, "{a}"))))
            .Rows.Should().ContainSingle();
        (await fx.Operations.PreviewRowsAsync(fx.Secret(), null, Req(oid, new PgFilter("label", PgFilterOp.IsNull, null))))
            .Rows.Select(r => r[0].Display).Should().Equal("2");
        (await fx.Operations.PreviewRowsAsync(fx.Secret(), null, Req(oid, new PgFilter("label", PgFilterOp.IsNotNull, null))))
            .Rows.Should().HaveCount(2);
        (await fx.Operations.PreviewRowsAsync(fx.Secret(), null, Req(oid, new PgFilter("label", PgFilterOp.ILike, "ONE"))))
            .Rows.Should().ContainSingle();
        (await fx.Operations.PreviewRowsAsync(fx.Secret(), null, Req(oid, new PgFilter("label", PgFilterOp.Like, "ONE"))))
            .Rows.Should().BeEmpty();
    }

    [DockerFact]
    public async Task Cells_are_formatted_by_kind()
    {
        var page = await fx.Operations.PreviewRowsAsync(fx.Secret(), null, Req(await OidAsync("samples")));
        var first = page.Rows[0];
        var second = page.Rows[1];

        var payload = first[Col(page, "payload")];
        payload.Kind.Should().Be(PgCellKind.Json);
        payload.Display.Should().Be("{…} 2 keys");
        payload.JsonKeyCount.Should().Be(2);
        payload.Full.Should().Contain("\n").And.Contain("\"k\": 1");
        second[Col(page, "payload")].Display.Should().Be("{…} 1 key");
        first[Col(page, "doc")].Display.Should().Be("[…] 3 items");
        first[Col(page, "doc")].JsonKeyCount.Should().Be(3);

        second[Col(page, "label")].Kind.Should().Be(PgCellKind.Null);
        second[Col(page, "label")].Display.Should().BeNull();

        var blob = first[Col(page, "blob")];
        blob.Kind.Should().Be(PgCellKind.Bytes);
        blob.Display.Should().Be("\\x000102030405060708090a0b0c0d0e0f… (18 bytes)");

        first[Col(page, "day")].Display.Should().Be("2024-03-05");
        first[Col(page, "ts")].Display.Should().Be("2024-03-05 10:11:12");
        first[Col(page, "tags")].Display.Should().Be("{a,b}");

        var longText = first[Col(page, "long_text")];
        longText.Kind.Should().Be(PgCellKind.Text);
        longText.Display!.Length.Should().BeLessThanOrEqualTo(201);
        longText.Display.Should().EndWith("…");
        longText.Full.Should().Be(new string('x', 300));
        first[Col(page, "label")].Full.Should().BeNull();
    }

    [DockerFact]
    public async Task Preview_of_a_missing_object_is_refused() =>
        await FluentActions.Awaiting(() => fx.Operations.PreviewRowsAsync(fx.Secret(), null, Req(4_000_000_000)))
            .Should().ThrowAsync<PgSqlRefusedException>();

    [DockerFact]
    public async Task Hostile_filter_column_is_refused_before_any_sql_runs()
    {
        var oid = await OidAsync("orders");
        await FluentActions.Awaiting(() => fx.Operations.PreviewRowsAsync(fx.Secret(), null,
                Req(oid, new PgFilter("id; drop table orders", PgFilterOp.Eq, "1"))))
            .Should().ThrowAsync<PgSqlRefusedException>();
    }

    [DockerFact]
    public async Task Export_writes_header_rows_and_rfc4180_quoting()
    {
        var csv = await fx.Operations.ExportTableCsvAsync(fx.Secret(), null, Req(await OidAsync("samples")));

        csv.Truncated.Should().BeFalse();
        csv.RowCount.Should().Be(3);
        csv.FileName.Should().MatchRegex(@"^public\.samples-\d{8}-\d{6}\.csv$");
        var text = Encoding.UTF8.GetString(csv.Content);
        text.Should().StartWith("id,label,tags,payload,doc,blob,day,ts,long_text,\"we\"\"ird\"\r\n");
        text.Should().Contain("\"a,\"\"b\"\"\nc\"");
        text.Should().Contain("1,one,\"{a,b}\"");
        text.Should().Contain("2,,,");
    }

    [DockerFact]
    public async Task Export_applies_filters_and_ignores_paging()
    {
        var csv = await fx.Operations.ExportTableCsvAsync(fx.Secret(), null,
            Req(await OidAsync("orders"), new PgFilter("id", PgFilterOp.Lte, "120")) with { Limit = 5, Offset = 50 });
        csv.RowCount.Should().Be(120);
        csv.Truncated.Should().BeFalse();
    }

    [DockerFact]
    public async Task Export_truncates_at_the_row_cap()
    {
        var oid = await OidAsync("orders");
        CsvExport.MaxRowsOverride = 10;
        try
        {
            var csv = await fx.Operations.ExportTableCsvAsync(fx.Secret(), null, Req(oid));
            csv.RowCount.Should().Be(10);
            csv.Truncated.Should().BeTrue();
            Encoding.UTF8.GetString(csv.Content).Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(11);
        }
        finally
        {
            CsvExport.MaxRowsOverride = null;
        }
    }

    [DockerFact]
    public async Task Export_at_exactly_the_cap_is_not_truncated()
    {
        var oid = await OidAsync("samples");
        CsvExport.MaxRowsOverride = 3;
        try
        {
            var csv = await fx.Operations.ExportTableCsvAsync(fx.Secret(), null, Req(oid));
            csv.RowCount.Should().Be(3);
            csv.Truncated.Should().BeFalse();
        }
        finally
        {
            CsvExport.MaxRowsOverride = null;
        }
    }
}
