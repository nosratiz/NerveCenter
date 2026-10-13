using FluentAssertions;
using SbConsole.Plugins.Sqlite.Client;

namespace SbConsole.Plugins.Sqlite.Tests.Client;

/// <summary>Design spec §12: the schema the Query editor's completion works from.</summary>
public sealed class SqliteOperationsCompletionSchemaTests : IDisposable
{
    private readonly TempDatabase _db = new(""""
        CREATE TABLE tenants(id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE);
        CREATE TABLE events(id INTEGER PRIMARY KEY, tenant_id INTEGER NOT NULL, name TEXT, ts INTEGER,
            day TEXT GENERATED ALWAYS AS (date(ts / 1000, 'unixepoch')) VIRTUAL);
        CREATE TABLE kv(k TEXT, scope TEXT, v BLOB, PRIMARY KEY (scope, k)) WITHOUT ROWID;
        CREATE TABLE "my table"("odd ""col""" TEXT, [order] INTEGER);
        CREATE INDEX idx_events_ts ON events(ts);
        CREATE VIEW v_tenant_events AS SELECT t.name AS tenant, COUNT(*) AS n FROM events e JOIN tenants t ON t.id = e.tenant_id GROUP BY t.id;
        CREATE TRIGGER trg AFTER INSERT ON events BEGIN SELECT 1; END;
        CREATE TABLE seq(id INTEGER PRIMARY KEY AUTOINCREMENT);
        """");

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Lists_tables_and_views_by_name_without_indexes_triggers_or_sqlite_internals()
    {
        var schema = await _db.Operations().GetCompletionSchemaAsync(_db.Secret());

        schema.Objects.Select(o => (o.Name, o.Type)).Should().Equal(
            ("events", SchemaObjectType.Table),
            ("kv", SchemaObjectType.Table),
            ("my table", SchemaObjectType.Table),
            ("seq", SchemaObjectType.Table),
            ("tenants", SchemaObjectType.Table),
            ("v_tenant_events", SchemaObjectType.View));
        schema.Capped.Should().BeFalse();
    }

    [Fact]
    public async Task Columns_carry_declared_type_and_primary_key_including_generated_columns()
    {
        var schema = await _db.Operations().GetCompletionSchemaAsync(_db.Secret());

        schema.Objects.Single(o => o.Name == "events").Columns.Should().Equal(
            new CompletionColumn("id", "INTEGER", true),
            new CompletionColumn("tenant_id", "INTEGER", false),
            new CompletionColumn("name", "TEXT", false),
            new CompletionColumn("ts", "INTEGER", false),
            new CompletionColumn("day", "TEXT", false));
    }

    [Fact]
    public async Task Without_rowid_composite_keys_and_view_columns()
    {
        var schema = await _db.Operations().GetCompletionSchemaAsync(_db.Secret());

        schema.Objects.Single(o => o.Name == "kv").Columns.Should().Equal(
            new CompletionColumn("k", "TEXT", true),
            new CompletionColumn("scope", "TEXT", true),
            new CompletionColumn("v", "BLOB", false));
        schema.Objects.Single(o => o.Name == "v_tenant_events").Columns.Select(c => c.Name).Should().Equal("tenant", "n");
    }

    [Fact]
    public async Task Quoted_and_odd_names_come_back_unquoted()
    {
        var schema = await _db.Operations().GetCompletionSchemaAsync(_db.Secret());

        schema.Objects.Single(o => o.Name == "my table").Columns.Select(c => c.Name).Should().Equal("odd \"col\"", "order");
    }

    [Fact]
    public async Task A_broken_view_is_listed_without_columns_instead_of_failing_the_schema()
    {
        _db.Execute("CREATE TABLE gone(x); CREATE VIEW v_broken AS SELECT x FROM gone; DROP TABLE gone;");

        var schema = await _db.Operations().GetCompletionSchemaAsync(_db.Secret());

        schema.Objects.Single(o => o.Name == "v_broken").Columns.Should().BeEmpty();
        schema.Objects.Should().Contain(o => o.Name == "events");
    }

    [Fact]
    public async Task Objects_and_columns_are_capped()
    {
        using var wide = new TempDatabase();
        var columns = string.Join(", ", Enumerable.Range(1, CompletionSchema.MaxColumns + 5).Select(i => $"c{i}"));
        var ddl = string.Join("\n", Enumerable.Range(1, CompletionSchema.MaxObjects + 3).Select(i => $"CREATE TABLE t{i:D4}(a, b);"));
        wide.Execute($"CREATE TABLE a_wide({columns});\n{ddl}");

        var schema = await wide.Operations().GetCompletionSchemaAsync(wide.Secret());

        schema.Objects.Should().HaveCount(CompletionSchema.MaxObjects);
        schema.Objects[0].Name.Should().Be("a_wide");
        schema.Objects[0].Columns.Should().HaveCount(CompletionSchema.MaxColumns);
        schema.Capped.Should().BeTrue();
    }

    [Fact]
    public async Task Reads_a_backup_file_instead_of_the_live_file()
    {
        var operations = _db.Operations();
        var secret = _db.Secret(backupDir: _db.BackupDir);
        var created = await operations.CreateBackupAsync(secret, new DateTimeOffset(2026, 10, 13, 2, 0, 0, TimeSpan.Zero));
        _db.Execute("CREATE TABLE only_live(x);");

        var live = await operations.GetCompletionSchemaAsync(secret);
        var backup = await operations.GetCompletionSchemaAsync(secret, created.FileName);

        live.Objects.Should().Contain(o => o.Name == "only_live");
        backup.Objects.Should().NotContain(o => o.Name == "only_live").And.Contain(o => o.Name == "events");
    }

    [Fact]
    public async Task A_missing_backup_file_fails()
    {
        var act = () => _db.Operations().GetCompletionSchemaAsync(_db.Secret(backupDir: _db.BackupDir), "gone.db");

        await act.Should().ThrowAsync<SqliteBackupException>();
    }
}
