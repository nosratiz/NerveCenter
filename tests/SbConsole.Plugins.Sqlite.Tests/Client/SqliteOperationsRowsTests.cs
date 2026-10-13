using FluentAssertions;
using SbConsole.Plugins.Sqlite.Client;

namespace SbConsole.Plugins.Sqlite.Tests.Client;

public sealed class SqliteOperationsRowsTests : IDisposable
{
    private readonly TempDatabase _db = new("""
        CREATE TABLE events(id INTEGER PRIMARY KEY, name TEXT, ts INTEGER, props TEXT, payload BLOB, score REAL);
        INSERT INTO events VALUES
          (1, 'page.view', 1759052466780, '{"path":"/a"}', NULL, 1.5),
          (2, 'page.view', 1759052468112, '{"path":"/b"}', x'00010203', NULL),
          (3, 'export.csv', 1759052461004, 'not json', NULL, NULL),
          (4, NULL, 1759052455201, NULL, NULL, NULL);
        CREATE TABLE kv(k TEXT PRIMARY KEY, v TEXT) WITHOUT ROWID;
        INSERT INTO kv VALUES ('a', '1'), ('c', '3'), ('b', '2');
        CREATE VIEW v_names AS SELECT name FROM events;
        """);

    [Fact]
    public async Task Preview_returns_newest_rows_first_with_the_sql_it_ran()
    {
        var page = await _db.Operations().PreviewRowsAsync(_db.Secret(), "events", 2);

        page.Sql.Should().Be("SELECT * FROM \"events\" ORDER BY rowid DESC LIMIT 2");
        page.Columns.Should().Equal("id", "name", "ts", "props", "payload", "score");
        page.Rows.Select(r => r[0].Display).Should().Equal("4", "3");
    }

    [Fact]
    public async Task Preview_renders_null_blob_real_and_integer_cells()
    {
        var page = await _db.Operations().PreviewRowsAsync(_db.Secret(), "events", 50);
        var row2 = page.Rows.Single(r => r[0].Display == "2");
        var row1 = page.Rows.Single(r => r[0].Display == "1");

        row2[4].Should().Be(new CellValue(CellKind.Blob, "BLOB · 4 bytes"));
        row2[5].Should().Be(new CellValue(CellKind.Null, "NULL"));
        row1[5].Should().Be(new CellValue(CellKind.Real, "1.5"));
        row1[2].Should().Be(new CellValue(CellKind.Integer, "1759052466780", 1759052466780));
    }

    [Fact]
    public async Task Preview_truncates_long_text()
    {
        _db.Execute($"INSERT INTO events(id, props) VALUES (5, '{new string('x', 500)}');");

        var page = await _db.Operations().PreviewRowsAsync(_db.Secret(), "events", 1);

        page.Rows[0][3].Truncated.Should().BeTrue();
        page.Rows[0][3].Display.Should().HaveLength(SqliteOperations.MaxCellText + 1).And.EndWith("…");
    }

    [Fact]
    public async Task Preview_orders_without_rowid_tables_by_primary_key_and_views_not_at_all()
    {
        var kv = await _db.Operations().PreviewRowsAsync(_db.Secret(), "kv", 50);
        var view = await _db.Operations().PreviewRowsAsync(_db.Secret(), "v_names", 50);

        kv.Sql.Should().Be("SELECT * FROM \"kv\" ORDER BY \"k\" DESC LIMIT 50");
        kv.Rows.Select(r => r[0].Display).Should().Equal("c", "b", "a");
        view.Sql.Should().Be("SELECT * FROM \"v_names\" LIMIT 50");
    }

    [Fact]
    public async Task Count_is_exact()
    {
        (await _db.Operations().CountRowsAsync(_db.Secret(), "events")).Should().Be(4);
    }

    [Fact]
    public async Task Count_refuses_names_not_in_the_schema()
    {
        var act = () => _db.Operations().CountRowsAsync(_db.Secret(), "events; DROP TABLE events");

        await act.Should().ThrowAsync<SqliteObjectNotFoundException>();
    }

    [Fact]
    public async Task Samples_count_json_epoch_ms_and_distinct_values()
    {
        var samples = await _db.Operations().SampleColumnsAsync(_db.Secret(), "events", 1000);

        samples.Single(s => s.Column == "props").Should().Be(new ColumnSample("props", 4, 3, 3, 3, 2, 0, 0));
        samples.Single(s => s.Column == "ts").Should().Be(new ColumnSample("ts", 4, 4, 4, 0, 0, 4, 4));
        samples.Single(s => s.Column == "name").Should().Be(new ColumnSample("name", 4, 3, 2, 3, 0, 0, 0));
    }

    [Fact]
    public async Task Samples_respect_the_sample_size()
    {
        var samples = await _db.Operations().SampleColumnsAsync(_db.Secret(), "events", 2);

        samples.Should().OnlyContain(s => s.SampledRows == 2);
    }

    public void Dispose() => _db.Dispose();
}
