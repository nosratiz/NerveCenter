using FluentAssertions;
using Microsoft.Data.Sqlite;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tests.Client;

/// <summary>Design spec §13: creating a database file under a configured root, against real temp directories.</summary>
public sealed class SqliteOperationsNewDatabaseTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sbc-newdb-").FullName;

    public SqliteOperationsNewDatabaseTests()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Path.Combine(Root, "team"));
        Directory.CreateDirectory(Path.Combine(_dir, "outside"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private string Root => Path.Combine(_dir, "data");

    private SqliteOperations Operations(params string[] roots) => new(new RootsPolicy(roots.Length == 0 ? [Root] : roots));

    private Task<NewDatabaseCreated> CreateAsync(NewDatabaseRequest request) => Operations().CreateDatabaseAsync(request);

    private string[] FilesUnderRoot() => Directory.GetFileSystemEntries(Root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();

    private static SqliteConnection Open(string path)
    {
        var c = new SqliteConnection($"Data Source={path};Mode=ReadWrite;Pooling=False;Foreign Keys=True");
        c.Open();
        return c;
    }

    private static long Long(string path, string sql)
    {
        using var c = Open(path);
        return SqliteSql.Long(c, sql);
    }

    private static string Text(string path, string sql)
    {
        using var c = Open(path);
        return SqliteSql.Text(c, sql);
    }

    // ---- happy paths ----

    [Fact]
    public async Task Empty_creates_a_wal_mode_file_with_no_tables_and_returns_its_configured_path()
    {
        var created = await CreateAsync(new NewDatabaseRequest(Root, null, "reports"));

        created.FileName.Should().Be("reports.db");
        created.Path.Should().Be(Path.Combine(Root, "reports.db"));
        created.Tables.Should().Be(0);
        File.Exists(created.ResolvedPath).Should().BeTrue();
        Text(created.ResolvedPath, "PRAGMA journal_mode").Should().Be("wal");
        Long(created.ResolvedPath, "SELECT COUNT(*) FROM sqlite_schema").Should().Be(0);
    }

    [Fact]
    public async Task Delete_journal_mode_is_honoured_and_leaves_no_siblings()
    {
        var created = await CreateAsync(new NewDatabaseRequest(Root, null, "plain.db", NewDatabaseJournal.Delete));

        Text(created.ResolvedPath, "PRAGMA journal_mode").Should().Be("delete");
        FilesUnderRoot().Should().Equal(Path.Combine(Root, "plain.db"), Path.Combine(Root, "team"));
    }

    [Theory]
    [InlineData("reports", "reports.db")]
    [InlineData("reports.db", "reports.db")]
    [InlineData("reports.SQLITE", "reports.SQLITE")]
    [InlineData("reports.sqlite3", "reports.sqlite3")]
    [InlineData("reports.v2", "reports.v2.db")]
    [InlineData("  spaced name  ", "spaced name.db")]
    public async Task The_db_extension_is_appended_unless_one_is_given(string fileName, string expected)
    {
        var created = await CreateAsync(new NewDatabaseRequest(Root, null, fileName));

        created.FileName.Should().Be(expected);
        File.Exists(Path.Combine(Root, expected)).Should().BeTrue();
    }

    [Fact]
    public async Task An_existing_sub_folder_is_used()
    {
        var created = await CreateAsync(new NewDatabaseRequest(Root, "team", "tasks"));

        created.Path.Should().Be(Path.Combine(Root, "team", "tasks.db"));
        File.Exists(Path.Combine(Root, "team", "tasks.db")).Should().BeTrue();
    }

    [Fact]
    public async Task Starter_schema_has_users_projects_tasks_with_foreign_keys_an_index_a_view_rows_and_statistics()
    {
        var created = await CreateAsync(new NewDatabaseRequest(Root, null, "starter", Template: NewDatabaseTemplate.Starter));
        var path = created.ResolvedPath;

        created.Tables.Should().Be(3);
        using (var c = Open(path))
        {
            SqliteSql.Rows(c, "SELECT type || ':' || name FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%' ORDER BY type, name", r => r.GetString(0))
                .Should().Equal("index:idx_tasks_project_status", "table:projects", "table:tasks", "table:users", "view:v_open_tasks");
            SqliteSql.Rows(c, "SELECT \"table\" || ':' || on_delete FROM pragma_foreign_key_list('tasks') ORDER BY \"table\"", r => r.GetString(0))
                .Should().Equal("projects:CASCADE", "users:SET NULL");
            SqliteSql.Rows(c, "SELECT \"table\" || ':' || on_delete FROM pragma_foreign_key_list('projects')", r => r.GetString(0))
                .Should().Equal("users:RESTRICT");
            var rows = SqliteSql.Long(c, "SELECT (SELECT COUNT(*) FROM users) + (SELECT COUNT(*) FROM projects) + (SELECT COUNT(*) FROM tasks)");
            rows.Should().BeInRange(10, 14);
            SqliteSql.Long(c, "SELECT COUNT(*) FROM sqlite_stat1").Should().BeGreaterThan(0, "ANALYZE ran");
            SqliteSql.Rows(c, "PRAGMA foreign_key_check", r => r.GetString(0)).Should().BeEmpty();
            SqliteSql.Long(c, "SELECT COUNT(*) FROM v_open_tasks").Should().BeGreaterThan(0);

            // Foreign keys are enforced: deleting a project cascades to its tasks; an orphan task is refused.
            var projectTasks = SqliteSql.Long(c, "SELECT COUNT(*) FROM tasks WHERE project_id = 1");
            projectTasks.Should().BeGreaterThan(0);
            SqliteSql.Execute(c, "DELETE FROM projects WHERE id = 1");
            SqliteSql.Long(c, "SELECT COUNT(*) FROM tasks WHERE project_id = 1").Should().Be(0);
            var orphan = () => SqliteSql.Execute(c, "INSERT INTO tasks(project_id, title, status) VALUES (999, 'x', 'todo')");
            orphan.Should().Throw<SqliteException>().Which.SqliteErrorCode.Should().Be(19);
        }
    }

    [Fact]
    public async Task My_sql_script_runs_every_statement()
    {
        var created = await CreateAsync(new NewDatabaseRequest(Root, null, "mine", Template: NewDatabaseTemplate.Script, Script: """
            CREATE TABLE notes(id INTEGER PRIMARY KEY, body TEXT NOT NULL);
            CREATE TABLE tags(id INTEGER PRIMARY KEY, note_id INTEGER REFERENCES notes(id), name TEXT);
            INSERT INTO notes(body) VALUES ('hello'), ('world');
            CREATE TRIGGER t_notes AFTER INSERT ON notes BEGIN INSERT INTO tags(note_id, name) VALUES (new.id, 'auto'); END;
            INSERT INTO notes(body) VALUES ('third');
            """));

        created.Tables.Should().Be(2);
        Long(created.ResolvedPath, "SELECT COUNT(*) FROM notes").Should().Be(3);
        Long(created.ResolvedPath, "SELECT COUNT(*) FROM tags").Should().Be(1);
    }

    // ---- script refusals and failures ----

    [Theory]
    [InlineData("ATTACH '/tmp/x.db' AS x")]
    [InlineData("CREATE TABLE a(x); VACUUM")]
    [InlineData("BEGIN; CREATE TABLE a(x); COMMIT")]
    [InlineData("   -- nothing\n")]
    public async Task A_refused_script_creates_no_file(string script)
    {
        var act = () => CreateAsync(new NewDatabaseRequest(Root, null, "refused", Template: NewDatabaseTemplate.Script, Script: script));

        await act.Should().ThrowAsync<SqliteScriptRefusedException>();
        FilesUnderRoot().Should().Equal(Path.Combine(Root, "team"));
    }

    [Fact]
    public async Task A_script_that_fails_part_way_deletes_the_file_and_its_siblings()
    {
        var act = () => CreateAsync(new NewDatabaseRequest(Root, null, "broken", Template: NewDatabaseTemplate.Script,
            Script: "CREATE TABLE a(x); INSERT INTO a VALUES (1); INSERT INTO missing VALUES (2);"));

        (await act.Should().ThrowAsync<SqliteException>()).Which.Message.Should().Contain("missing");
        FilesUnderRoot().Should().Equal(Path.Combine(Root, "team"));
    }

    // ---- folder and sub-folder ----

    [Fact]
    public async Task With_no_roots_configured_nothing_can_be_created()
    {
        var act = () => new SqliteOperations(new RootsPolicy([])).CreateDatabaseAsync(new NewDatabaseRequest(Root, null, "x"));

        (await act.Should().ThrowAsync<SqliteNewDatabaseRefusedException>()).Which.Message.Should().Contain("SBC_SQLITE_ROOTS");
        FilesUnderRoot().Should().Equal(Path.Combine(Root, "team"));
    }

    [Fact]
    public async Task The_folder_must_be_one_of_the_configured_roots_verbatim()
    {
        foreach (var folder in new[] { Path.Combine(_dir, "outside"), Root + "/", Path.Combine(Root, "team"), "" })
        {
            var act = () => CreateAsync(new NewDatabaseRequest(folder, null, "x"));

            (await act.Should().ThrowAsync<SqliteNewDatabaseRefusedException>()).Which.Message.Should().Contain("configured folders");
        }

        Directory.GetFiles(_dir, "*.db", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("team/../../outside")]
    [InlineData("/etc")]
    [InlineData("./team")]
    [InlineData("team\\x")]
    public async Task A_sub_folder_must_stay_inside_the_folder(string subFolder)
    {
        var act = () => CreateAsync(new NewDatabaseRequest(Root, subFolder, "x"));

        await act.Should().ThrowAsync<SqliteNewDatabaseRefusedException>();
        Directory.GetFiles(_dir, "*.db", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task A_missing_sub_folder_is_refused_and_never_created()
    {
        var act = () => CreateAsync(new NewDatabaseRequest(Root, "new/deeper", "x"));

        (await act.Should().ThrowAsync<SqliteNewDatabaseRefusedException>()).Which.Message.Should().Contain("doesn't exist");
        Directory.Exists(Path.Combine(Root, "new")).Should().BeFalse();
    }

    [Fact]
    public async Task A_missing_root_directory_is_refused_and_never_created()
    {
        var missing = Path.Combine(_dir, "not-there");

        var act = () => Operations(missing).CreateDatabaseAsync(new NewDatabaseRequest(missing, null, "x"));

        (await act.Should().ThrowAsync<SqliteNewDatabaseRefusedException>()).Which.Message.Should().Contain("doesn't exist");
        Directory.Exists(missing).Should().BeFalse();
    }

    [Fact]
    public async Task A_path_the_policy_refuses_is_refused_with_its_reason()
    {
        var policy = new RootsPolicy([Root]) { RefuseFile = "reports.db" };

        var act = () => new SqliteOperations(policy).CreateDatabaseAsync(new NewDatabaseRequest(Root, null, "reports"));

        (await act.Should().ThrowAsync<SqliteNewDatabaseRefusedException>()).Which.Message.Should().Be("belongs to SbConsole");
        File.Exists(Path.Combine(Root, "reports.db")).Should().BeFalse();
    }

    // ---- file name ----

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("..")]
    [InlineData("x..y")]
    [InlineData(".hidden")]
    [InlineData("a;b")]
    [InlineData("tab\tname")]
    public async Task Bad_file_names_are_refused_before_anything_is_created(string fileName)
    {
        var act = () => CreateAsync(new NewDatabaseRequest(Root, null, fileName));

        await act.Should().ThrowAsync<SqliteNewDatabaseRefusedException>();
        FilesUnderRoot().Should().Equal(Path.Combine(Root, "team"));
    }

    [Fact]
    public async Task An_overlong_file_name_is_refused()
    {
        var act = () => CreateAsync(new NewDatabaseRequest(Root, null, new string('a', NewDatabaseRules.MaxFileNameLength)));

        await act.Should().ThrowAsync<SqliteNewDatabaseRefusedException>();
    }

    // ---- never overwrites ----

    [Fact]
    public async Task An_existing_file_is_never_overwritten()
    {
        var existing = Path.Combine(Root, "reports.db");
        File.WriteAllBytes(existing, [1, 2, 3]);

        var act = () => CreateAsync(new NewDatabaseRequest(Root, null, "reports", Template: NewDatabaseTemplate.Starter));

        (await act.Should().ThrowAsync<SqliteNewDatabaseRefusedException>()).Which.Message.Should().Contain("already exists");
        File.ReadAllBytes(existing).Should().Equal(1, 2, 3);
    }

    [Theory]
    [InlineData("-wal")]
    [InlineData("-shm")]
    [InlineData("-journal")]
    public async Task A_leftover_sibling_file_is_refused_and_kept(string suffix)
    {
        var sibling = Path.Combine(Root, "reports.db" + suffix);
        File.WriteAllBytes(sibling, [7]);

        var act = () => CreateAsync(new NewDatabaseRequest(Root, null, "reports"));

        (await act.Should().ThrowAsync<SqliteNewDatabaseRefusedException>()).Which.Message.Should().Contain(suffix);
        File.Exists(Path.Combine(Root, "reports.db")).Should().BeFalse();
        File.ReadAllBytes(sibling).Should().Equal(7);
    }

    [Fact]
    public async Task A_directory_with_the_name_is_refused()
    {
        Directory.CreateDirectory(Path.Combine(Root, "taken.db"));

        var act = () => CreateAsync(new NewDatabaseRequest(Root, null, "taken"));

        await act.Should().ThrowAsync<SqliteNewDatabaseRefusedException>();
    }

    [Fact]
    public async Task A_dangling_symlink_with_the_name_is_refused()
    {
        var link = Path.Combine(Root, "link.db");
        File.CreateSymbolicLink(link, Path.Combine(_dir, "outside", "target.db"));

        var act = () => CreateAsync(new NewDatabaseRequest(Root, null, "link"));

        await act.Should().ThrowAsync<SqliteNewDatabaseRefusedException>();
        File.Exists(Path.Combine(_dir, "outside", "target.db")).Should().BeFalse();
    }

    // ---- delete and backups-dir helpers ----

    [Fact]
    public async Task DeleteCreatedDatabase_removes_the_file_and_its_siblings()
    {
        var created = await CreateAsync(new NewDatabaseRequest(Root, null, "undo"));
        File.WriteAllBytes(created.ResolvedPath + "-journal", [1]);

        await Operations().DeleteCreatedDatabaseAsync(created.Path);

        FilesUnderRoot().Should().Equal(Path.Combine(Root, "team"));
    }

    [Fact]
    public async Task DeleteCreatedDatabase_refuses_a_file_that_is_not_a_database_name()
    {
        var other = Path.Combine(Root, "notes.txt");
        File.WriteAllText(other, "keep");

        var act = () => Operations().DeleteCreatedDatabaseAsync(other);

        await act.Should().ThrowAsync<SqliteNewDatabaseRefusedException>();
        File.Exists(other).Should().BeTrue();
    }

    [Fact]
    public async Task FindBackupsDir_offers_the_backups_folder_only_when_it_exists()
    {
        var operations = Operations();
        (await operations.FindBackupsDirAsync(Root, null)).Should().BeNull();

        Directory.CreateDirectory(Path.Combine(Root, "backups"));
        Directory.CreateDirectory(Path.Combine(Root, "team", "backups"));

        (await operations.FindBackupsDirAsync(Root, null)).Should().Be(Path.Combine(Root, "backups"));
        (await operations.FindBackupsDirAsync(Root, "team")).Should().Be(Path.Combine(Root, "team", "backups"));
        (await operations.FindBackupsDirAsync(Path.Combine(_dir, "outside"), null)).Should().BeNull();
        (await operations.FindBackupsDirAsync(Root, "../outside")).Should().BeNull();
    }

    /// <summary>Allows full paths lexically under a root (no symlink resolution -- the host's HostPathPolicy does that).</summary>
    private sealed class RootsPolicy(IReadOnlyList<string> roots) : IHostPathPolicy
    {
        public string? RefuseFile { get; init; }

        public IReadOnlyList<string> AllowedRoots => roots;

        public PathCheckResult Check(string path)
        {
            if (roots.Count == 0)
            {
                return PathCheckResult.Refuse("No SQLite roots are configured — set SBC_SQLITE_ROOTS.");
            }

            var full = Path.GetFullPath(path);
            if (RefuseFile is not null && Path.GetFileName(full) == RefuseFile)
            {
                return PathCheckResult.Refuse("belongs to SbConsole");
            }

            return roots.Any(r => full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(r)) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                ? PathCheckResult.Allow(full)
                : PathCheckResult.Refuse("outside the roots");
        }
    }
}
