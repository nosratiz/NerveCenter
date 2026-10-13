using FluentAssertions;
using SbConsole.Plugins.Sqlite.Client;

namespace SbConsole.Plugins.Sqlite.Tests.Client;

public sealed class SqliteOperationsSchemaTests : IDisposable
{
    private readonly TempDatabase _db = new("""
        CREATE TABLE tenants(id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE);
        CREATE TABLE events(
            id INTEGER PRIMARY KEY,
            tenant_id INTEGER NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
            name TEXT NOT NULL,
            source TEXT NOT NULL DEFAULT 'web',
            ts INTEGER NOT NULL);
        CREATE INDEX idx_events_tenant_ts ON events(tenant_id, ts);
        CREATE TABLE kv(k TEXT PRIMARY KEY, v BLOB) WITHOUT ROWID;
        CREATE VIEW v_tenant_events AS SELECT t.name, COUNT(*) AS n FROM events e JOIN tenants t ON t.id = e.tenant_id GROUP BY t.id;
        CREATE TRIGGER trg_events_ai AFTER INSERT ON events BEGIN SELECT 1; END;
        INSERT INTO tenants VALUES (1, 'acme'), (2, 'globex');
        INSERT INTO events VALUES (1, 1, 'page.view', 'web', 1759052466780), (2, 2, 'report.viewed', 'api', 1759052468112);
        """);

    [Fact]
    public async Task Listing_groups_schema_objects_and_hides_sqlite_internals()
    {
        var listing = await _db.Operations().ListSchemaAsync(_db.Secret());

        listing.Tables.Select(t => t.Name).Should().Equal("events", "kv", "tenants");
        listing.Views.Select(v => v.Name).Should().Equal("v_tenant_events");
        listing.Count(SchemaObjectType.Index).Should().Be(1, "only idx_events_tenant_ts -- the UNIQUE autoindex is internal");
        listing.Count(SchemaObjectType.Trigger).Should().Be(1);
        listing.SizesAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task Row_estimates_appear_only_after_analyze()
    {
        (await _db.Operations().ListSchemaAsync(_db.Secret())).Tables.Should().OnlyContain(t => t.EstimatedRows == null);

        _db.Execute("ANALYZE;");
        var listing = await _db.Operations().ListSchemaAsync(_db.Secret());

        listing.Tables.Single(t => t.Name == "events").EstimatedRows.Should().Be(2);
    }

    [Fact]
    public async Task Detail_reads_columns_indexes_foreign_keys_and_ddl()
    {
        var detail = await _db.Operations().GetTableDetailAsync(_db.Secret(), "events");

        detail.Type.Should().Be(SchemaObjectType.Table);
        detail.WithoutRowId.Should().BeFalse();
        detail.HasRowId.Should().BeTrue();
        detail.Ddl.Should().StartWith("CREATE TABLE events(");
        detail.Columns.Select(c => c.Name).Should().Equal("id", "tenant_id", "name", "source", "ts");
        detail.Columns[0].Should().Be(new ColumnInfo("id", "INTEGER", false, null, 1, false, false));
        detail.Columns[3].DefaultValue.Should().Be("'web'");
        detail.Columns[1].NotNull.Should().BeTrue();
        detail.Indexes.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new IndexInfo("idx_events_tenant_ts", false, "c", false, ["tenant_id", "ts"]));
        detail.ForeignKeys.Should().ContainSingle().Which.Should().Be(
            new ForeignKeyInfo(0, "tenant_id", "tenants", "id", "NO ACTION", "CASCADE"));
    }

    [Fact]
    public async Task Detail_flags_without_rowid_tables()
    {
        var detail = await _db.Operations().GetTableDetailAsync(_db.Secret(), "kv");

        detail.WithoutRowId.Should().BeTrue();
        detail.HasRowId.Should().BeFalse();
        detail.Indexes.Should().ContainSingle().Which.Origin.Should().Be("pk");
    }

    [Fact]
    public async Task Detail_of_a_view_has_columns_but_no_indexes_or_foreign_keys()
    {
        var detail = await _db.Operations().GetTableDetailAsync(_db.Secret(), "v_tenant_events");

        detail.Type.Should().Be(SchemaObjectType.View);
        detail.Columns.Select(c => c.Name).Should().Equal("name", "n");
        detail.Indexes.Should().BeEmpty();
        detail.ForeignKeys.Should().BeEmpty();
        detail.HasRowId.Should().BeFalse();
    }

    [Fact]
    public async Task Detail_of_an_unknown_name_throws_not_found()
    {
        var act = () => _db.Operations().GetTableDetailAsync(_db.Secret(), "nope");

        await act.Should().ThrowAsync<SqliteObjectNotFoundException>();
    }

    [Fact]
    public async Task Names_needing_quotes_are_handled()
    {
        _db.Execute("CREATE TABLE \"we\"\"ird name\" (x);");

        var detail = await _db.Operations().GetTableDetailAsync(_db.Secret(), "we\"ird name");

        detail.Columns.Should().ContainSingle().Which.Name.Should().Be("x");
    }

    [Fact]
    public async Task Listing_reports_the_main_file_size_for_the_empty_state()
    {
        var listing = await _db.Operations().ListSchemaAsync(_db.Secret());

        listing.FileBytes.Should().Be(new FileInfo(_db.Path).Length);
    }

    public void Dispose() => _db.Dispose();
}
