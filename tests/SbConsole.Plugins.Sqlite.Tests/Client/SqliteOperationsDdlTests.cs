using FluentAssertions;
using SbConsole.Plugins.Sqlite.Client;

namespace SbConsole.Plugins.Sqlite.Tests.Client;

/// <summary>Design spec §11.5: Create / Rename / Drop table, drop impact, and §11.6 REINDEX.</summary>
public sealed class SqliteOperationsDdlTests : IDisposable
{
    private readonly TempDatabase _db = new("""
        CREATE TABLE tenants(id INTEGER PRIMARY KEY, name TEXT, UNIQUE (id, name));
        CREATE TABLE sessions(id INTEGER PRIMARY KEY, tenant_id INTEGER REFERENCES tenants(id) ON DELETE CASCADE);
        CREATE TABLE invoices(id INTEGER PRIMARY KEY, tenant_id INTEGER REFERENCES tenants(id) ON DELETE RESTRICT);
        CREATE TABLE notes(id INTEGER PRIMARY KEY, tenant_id INTEGER, region TEXT, FOREIGN KEY (tenant_id, region) REFERENCES tenants(id, name) ON DELETE SET NULL);
        CREATE INDEX idx_tenants_name ON tenants(name);
        CREATE VIEW v_tenants AS SELECT * FROM tenants;
        INSERT INTO tenants VALUES (1, 'acme'), (2, 'globex');
        INSERT INTO sessions VALUES (10, 1), (11, 1), (20, 2);
        """);

    private string WriteSecret => _db.Secret(mode: SqliteAccessMode.ReadWrite);

    private string ReadSecret => _db.Secret();

    private long Scalar(string sql)
    {
        using var check = _db.OpenWriter();
        return SqliteSql.Long(check, sql);
    }

    private bool Exists(string name) => Scalar($"SELECT COUNT(*) FROM sqlite_schema WHERE name = '{name}'") > 0;

    // ---- create ----

    [Fact]
    public async Task Create_runs_one_create_table_statement_and_returns_its_name()
    {
        var name = await _db.Operations().CreateTableAsync(WriteSecret, "-- the new one\nCREATE TABLE \"report runs\"(id INTEGER PRIMARY KEY, at TEXT UNIQUE);");

        name.Should().Be("report runs");
        Exists("report runs").Should().BeTrue();
    }

    [Theory]
    [InlineData("CREATE VIEW v2 AS SELECT 1")]
    [InlineData("CREATE INDEX i2 ON tenants(id)")]
    [InlineData("DROP TABLE tenants")]
    [InlineData("CREATE TABLE a(x); CREATE TABLE b(y)")]
    [InlineData("CREATE VIRTUAL TABLE f USING fts5(x)")]
    public async Task Create_refuses_anything_but_exactly_one_create_table(string ddl)
    {
        var act = () => _db.Operations().CreateTableAsync(WriteSecret, ddl);

        (await act.Should().ThrowAsync<SqliteDdlRefusedException>()).Which.Message.Should().Contain("exactly one CREATE TABLE");
        Exists("tenants").Should().BeTrue();
        Exists("a").Should().BeFalse();
    }

    [Fact]
    public async Task Create_refuses_a_table_that_already_exists_or_a_temp_table()
    {
        var ops = _db.Operations();

        (await FluentActions.Awaiting(() => ops.CreateTableAsync(WriteSecret, "CREATE TABLE IF NOT EXISTS tenants(id)"))
            .Should().ThrowAsync<SqliteDdlRefusedException>()).Which.Message.Should().Contain("already exist");
        await FluentActions.Awaiting(() => ops.CreateTableAsync(WriteSecret, "CREATE TEMP TABLE scratch(x)"))
            .Should().ThrowAsync<SqliteDdlRefusedException>();
    }

    [Fact]
    public async Task Create_refuses_script_level_problems_through_the_splitter()
    {
        await FluentActions.Awaiting(() => _db.Operations().CreateTableAsync(WriteSecret, "  "))
            .Should().ThrowAsync<SqliteScriptRefusedException>();
    }

    [Fact]
    public async Task Create_on_a_read_only_connection_is_refused_and_creates_nothing()
    {
        await FluentActions.Awaiting(() => _db.Operations().CreateTableAsync(ReadSecret, "CREATE TABLE x(y)"))
            .Should().ThrowAsync<SqliteReadOnlyConnectionException>();
        Exists("x").Should().BeFalse();
    }

    // ---- rename ----

    [Fact]
    public async Task Rename_renames_a_table_and_updates_references()
    {
        await _db.Operations().RenameTableAsync(WriteSecret, "tenants", "customers");

        Exists("customers").Should().BeTrue();
        Exists("tenants").Should().BeFalse();
        using var check = _db.OpenWriter();
        SqliteSql.Text(check, "SELECT sql FROM sqlite_schema WHERE name = 'sessions'").Should().Contain("\"customers\"");
    }

    [Theory]
    [InlineData("missing", "x", typeof(SqliteObjectNotFoundException))]
    [InlineData("v_tenants", "x", typeof(SqliteDdlRefusedException))]
    [InlineData("sqlite_schema", "x", typeof(SqliteDdlRefusedException))]
    [InlineData("tenants", "", typeof(SqliteDdlRefusedException))]
    [InlineData("tenants", "   ", typeof(SqliteDdlRefusedException))]
    [InlineData("tenants", "sqlite_mine", typeof(SqliteDdlRefusedException))]
    [InlineData("tenants", "SQLITE_x", typeof(SqliteDdlRefusedException))]
    [InlineData("tenants", "SESSIONS", typeof(SqliteDdlRefusedException))]
    [InlineData("tenants", "idx_tenants_name", typeof(SqliteDdlRefusedException))]
    [InlineData("tenants", "tenants", typeof(SqliteDdlRefusedException))]
    public async Task Rename_validates_both_names(string oldName, string newName, Type expected)
    {
        var act = () => _db.Operations().RenameTableAsync(WriteSecret, oldName, newName);

        (await act.Should().ThrowAsync<Exception>()).Which.Should().BeOfType(expected);
        Exists("tenants").Should().BeTrue();
    }

    [Fact]
    public async Task Rename_limits_the_new_name_to_128_characters()
    {
        var ops = _db.Operations();

        await FluentActions.Awaiting(() => ops.RenameTableAsync(WriteSecret, "tenants", new string('t', 129)))
            .Should().ThrowAsync<SqliteDdlRefusedException>();
        await ops.RenameTableAsync(WriteSecret, "tenants", new string('t', 128));
        Exists(new string('t', 128)).Should().BeTrue();
    }

    [Fact]
    public async Task Rename_to_a_different_case_of_the_same_name_is_refused_like_sqlite_does()
    {
        var act = () => _db.Operations().RenameTableAsync(WriteSecret, "tenants", "Tenants");

        (await act.Should().ThrowAsync<SqliteDdlRefusedException>()).Which.Message.Should().Contain("case-insensitively");
    }

    [Fact]
    public async Task Rename_on_a_read_only_connection_is_refused()
    {
        await FluentActions.Awaiting(() => _db.Operations().RenameTableAsync(ReadSecret, "tenants", "customers"))
            .Should().ThrowAsync<SqliteReadOnlyConnectionException>();
        Exists("tenants").Should().BeTrue();
    }

    // ---- drop impact ----

    [Fact]
    public async Task Drop_impact_lists_indexes_rows_and_every_referencing_table_with_its_consequence()
    {
        _db.Execute("ANALYZE");

        var impact = await _db.Operations().GetDropImpactAsync(ReadSecret, "tenants");

        impact.Table.Should().Be("tenants");
        impact.RowsApprox.Should().Be(2);
        impact.IndexCount.Should().Be(2); // idx_tenants_name + the UNIQUE autoindex
        impact.References.Select(r => (r.Table, string.Join(",", r.FromColumns), r.OnDelete, r.Consequence)).Should().Equal(
            ("invoices", "tenant_id", "RESTRICT", "the drop fails if any rows reference it"),
            ("notes", "tenant_id,region", "SET NULL", "their reference is cleared"),
            ("sessions", "tenant_id", "CASCADE", "rows there are deleted too"));
    }

    [Fact]
    public async Task Drop_impact_without_stat1_has_unknown_rows_and_no_references_for_a_leaf_table()
    {
        var impact = await _db.Operations().GetDropImpactAsync(ReadSecret, "sessions");

        impact.RowsApprox.Should().BeNull();
        impact.References.Should().BeEmpty();
    }

    [Theory]
    [InlineData("missing", typeof(SqliteObjectNotFoundException))]
    [InlineData("v_tenants", typeof(SqliteDdlRefusedException))]
    [InlineData("sqlite_stat1", typeof(SqliteDdlRefusedException))]
    public async Task Drop_impact_and_drop_reject_unknown_views_and_internal_names(string name, Type expected)
    {
        var ops = _db.Operations();

        (await FluentActions.Awaiting(() => ops.GetDropImpactAsync(ReadSecret, name)).Should().ThrowAsync<Exception>()).Which.Should().BeOfType(expected);
        (await FluentActions.Awaiting(() => ops.DropTableAsync(WriteSecret, name)).Should().ThrowAsync<Exception>()).Which.Should().BeOfType(expected);
        Exists("v_tenants").Should().BeTrue();
    }

    // ---- drop ----

    [Fact]
    public async Task Drop_with_a_cascade_child_deletes_the_child_rows()
    {
        await _db.Operations().DropTableAsync(WriteSecret, "sessions"); // leaf: no references
        _db.Execute("CREATE TABLE child(id INTEGER PRIMARY KEY, sid INTEGER REFERENCES parent(id) ON DELETE CASCADE); CREATE TABLE parent(id INTEGER PRIMARY KEY); INSERT INTO parent VALUES (1); INSERT INTO child VALUES (1, 1), (2, 1);");

        await _db.Operations().DropTableAsync(WriteSecret, "parent");

        Exists("sessions").Should().BeFalse();
        Exists("parent").Should().BeFalse();
        Scalar("SELECT COUNT(*) FROM child").Should().Be(0);
    }

    [Fact]
    public async Task Drop_fails_and_rolls_back_when_a_restrict_child_references_rows()
    {
        _db.Execute("INSERT INTO invoices VALUES (100, 1)");

        var act = () => _db.Operations().DropTableAsync(WriteSecret, "tenants");

        await act.Should().ThrowAsync<Microsoft.Data.Sqlite.SqliteException>();
        Exists("tenants").Should().BeTrue();
        Scalar("SELECT COUNT(*) FROM tenants").Should().Be(2);
        Scalar("SELECT COUNT(*) FROM sessions").Should().Be(3);
    }

    [Fact]
    public async Task Drop_on_a_read_only_connection_is_refused()
    {
        await FluentActions.Awaiting(() => _db.Operations().DropTableAsync(ReadSecret, "sessions"))
            .Should().ThrowAsync<SqliteReadOnlyConnectionException>();
        Exists("sessions").Should().BeTrue();
    }

    // ---- reindex ----

    [Fact]
    public async Task Reindex_rebuilds_indexes_then_runs_a_fresh_integrity_check()
    {
        var outcome = await _db.Operations().ReindexAsync(WriteSecret);

        outcome.IntegrityOk.Should().BeTrue();
        outcome.ForeignKeyViolations.Should().Be(0);
    }

    [Fact]
    public async Task Reindex_on_a_read_only_connection_is_refused()
    {
        await FluentActions.Awaiting(() => _db.Operations().ReindexAsync(ReadSecret))
            .Should().ThrowAsync<SqliteReadOnlyConnectionException>();
    }

    public void Dispose() => _db.Dispose();
}
