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
        first[Col(page, "ts")].Display.Should().Be("2024-03-05 10:11:12+00");
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

    [DockerFact]
    public async Task Unreadable_values_fall_back_to_the_servers_text_form_in_preview()
    {
        var page = await fx.Operations.PreviewRowsAsync(fx.Secret(), null, Req(await OidAsync("oddities")));
        var row = page.Rows[0];
        row[Col(page, "span")].Display.Should().Be("1 mon 2 days");
        row[Col(page, "num")].Display.Should().Be("NaN");
        row[Col(page, "big")].Display.Should().Be("10000000000000000000000000000000000000000");
        row[Col(page, "bc")].Display.Should().Be("0044-03-15 BC");
        page.Rows[1][Col(page, "num")].Display.Should().Be("Infinity");
        page.Columns[Col(page, "span")].TypeName.Should().Be("interval");
        page.Columns[Col(page, "code")].TypeName.Should().Be("character varying");
    }

    [DockerFact]
    public async Task Unreadable_values_do_not_fail_the_export()
    {
        var csv = await fx.Operations.ExportTableCsvAsync(fx.Secret(), null, Req(await OidAsync("oddities")));
        var text = Encoding.UTF8.GetString(csv.Content);
        text.Should().Contain("1 mon 2 days").And.Contain("NaN").And.Contain("0044-03-15 BC").And.Contain("Infinity");
    }

    [DockerFact]
    public async Task Filter_value_is_not_truncated_by_a_varchar_typmod_cast()
    {
        var oid = await OidAsync("oddities");
        (await fx.Operations.PreviewRowsAsync(fx.Secret(), null, Req(oid, new PgFilter("code", PgFilterOp.Eq, "ABCDEFG")))).Rows
            .Should().BeEmpty();
        (await fx.Operations.PreviewRowsAsync(fx.Secret(), null, Req(oid, new PgFilter("code", PgFilterOp.Eq, "ABCDE")))).Rows
            .Should().HaveCount(1);
    }

    [DockerFact]
    public async Task Export_over_the_cap_still_reports_truncated_with_a_bounded_query()
    {
        CsvExport.MaxRowsOverride = 10;
        try
        {
            var csv = await fx.Operations.ExportTableCsvAsync(fx.Secret(), null, Req(await OidAsync("orders")));
            csv.RowCount.Should().Be(10);
            csv.Truncated.Should().BeTrue();
        }
        finally
        {
            CsvExport.MaxRowsOverride = null;
        }
    }

    [DockerFact]
    public async Task Json_pretty_form_keeps_non_ascii_and_html_characters()
    {
        var page = await fx.Operations.PreviewRowsAsync(fx.Secret(), null, Req(await OidAsync("oddities")));
        var cell = page.Rows[0][Col(page, "j")];
        cell.Full.Should().Contain("José").And.Contain("<b>").And.Contain("it's");
    }

    private async Task<string?[]> SortedIdsAsync(string col, bool desc = false)
    {
        var page = await fx.Operations.PreviewRowsAsync(fx.Secret(), null,
            new PgRowsRequest(await OidAsync("sortable"), [], col, desc, 0, 50));
        return page.Rows.Select(r => r[Col(page, "id")].Display).ToArray();
    }

    [DockerFact]
    public async Task Sort_on_numeric_is_numeric_not_textual() =>
        (await SortedIdsAsync("num")).Should().Equal("2", "1", "3");

    [DockerFact]
    public async Task Sort_on_interval_timestamp_and_date_is_chronological()
    {
        (await SortedIdsAsync("span")).Should().Equal("2", "1", "3");
        (await SortedIdsAsync("ts")).Should().Equal("2", "3", "1");
        (await SortedIdsAsync("d", desc: true)).Should().Equal("1", "3", "2");
    }

    [DockerFact]
    public async Task Export_sorts_by_the_typed_column()
    {
        var csv = await fx.Operations.ExportTableCsvAsync(fx.Secret(), null,
            new PgRowsRequest(await OidAsync("sortable"), [], "num", false, 0, 50));
        var text = Encoding.UTF8.GetString(csv.Content);
        text.IndexOf("\n2,", StringComparison.Ordinal).Should().BeLessThan(text.IndexOf("\n1,", StringComparison.Ordinal));
        text.IndexOf("\n1,", StringComparison.Ordinal).Should().BeLessThan(text.IndexOf("\n3,", StringComparison.Ordinal));
    }

    [DockerFact]
    public async Task Char_n_and_bit_n_equality_filters_match()
    {
        var oid = await OidAsync("sortable");
        var ch = await fx.Operations.PreviewRowsAsync(fx.Secret(), null,
            new PgRowsRequest(oid, [new PgFilter("ch", PgFilterOp.Eq, "ab")], "id", false, 0, 50));
        ch.Rows.Should().HaveCount(1);
        var bit = await fx.Operations.PreviewRowsAsync(fx.Secret(), null,
            new PgRowsRequest(oid, [new PgFilter("b", PgFilterOp.Eq, "101")], "id", false, 0, 50));
        bit.Rows.Should().HaveCount(1);
    }

    [DockerFact]
    public async Task Json_and_jsonb_cells_are_labelled_by_their_shape()
    {
        var table = "json_shapes_" + Guid.NewGuid().ToString("N");
        await using var conn = new Npgsql.NpgsqlConnection(fx.AdminConnectionString());
        await conn.OpenAsync();
        try
        {
            await using (var create = new Npgsql.NpgsqlCommand($$"""
                CREATE TABLE {{table}} (id int PRIMARY KEY, jb jsonb, j json);
                INSERT INTO {{table}} VALUES
                    (1, '{"a":1,"b":2}', '{"a":1}'),
                    (2, '[1,2,3]', '[1, [2, [3, [4]]]]'),
                    (3, '[]', '[]'),
                    (4, '"hello"', '42'),
                    (5, 'true', 'null'),
                    (6, NULL, NULL);
                """, conn))
            {
                await create.ExecuteNonQueryAsync();
            }

            var tree = await fx.Operations.ListSchemaAsync(fx.Secret(), null);
            var oid = tree.Schemas.Single(s => s.Name == "public").Tables.Single(t => t.Name == table).Oid;
            var page = await fx.Operations.PreviewRowsAsync(fx.Secret(), null, Req(oid));
            var jb = Col(page, "jb");
            var j = Col(page, "j");

            page.Rows[0][jb].Display.Should().Be("{…} 2 keys");
            page.Rows[0][j].Display.Should().Be("{…} 1 key");
            page.Rows[1][jb].Display.Should().Be("[…] 3 items");
            page.Rows[1][j].Display.Should().Be("[…] 2 items");
            page.Rows[2][jb].Display.Should().Be("[…] 0 items");
            page.Rows[2][j].Display.Should().Be("[…] 0 items");
            page.Rows[3][jb].Should().Be(new PgCell(PgCellKind.Text, "\"hello\""));
            page.Rows[3][j].Should().Be(new PgCell(PgCellKind.Text, "42"));
            page.Rows[4][jb].Should().Be(new PgCell(PgCellKind.Text, "true"));
            page.Rows[4][j].Should().Be(new PgCell(PgCellKind.Text, "null"));
            page.Rows[5][jb].Kind.Should().Be(PgCellKind.Null);
        }
        finally
        {
            await using var drop = new Npgsql.NpgsqlCommand($"DROP TABLE IF EXISTS {table}", conn);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
