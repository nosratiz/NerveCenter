using FluentAssertions;
using SbConsole.Plugins.Sqlite.Client;

namespace SbConsole.Plugins.Sqlite.Tests.Client;

public class SqliteOperationsIntegrityTests
{
    [Fact]
    public async Task A_healthy_file_reports_ok_and_no_foreign_key_violations()
    {
        using var db = new TempDatabase("CREATE TABLE t(x); INSERT INTO t VALUES (1);");

        var outcome = await db.Operations().RunIntegrityCheckAsync(db.Secret());

        outcome.Messages.Should().Equal("ok");
        outcome.IntegrityOk.Should().BeTrue();
        outcome.ForeignKeyViolations.Should().Be(0);
    }

    [Fact]
    public async Task Orphaned_child_rows_are_counted_as_foreign_key_violations()
    {
        // foreign_keys is per-connection and off by default in SQLite, which is exactly how orphans get in.
        // Microsoft.Data.Sqlite turns it ON for every connection it opens, so the arranging connection switches it off.
        using var db = new TempDatabase("""
            PRAGMA foreign_keys = OFF;
            CREATE TABLE parent(id INTEGER PRIMARY KEY);
            CREATE TABLE child(id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES parent(id));
            INSERT INTO child VALUES (1, 99), (2, 98);
            """);

        var outcome = await db.Operations().RunIntegrityCheckAsync(db.Secret());

        outcome.IntegrityOk.Should().BeTrue();
        outcome.ForeignKeyViolations.Should().Be(2);
    }

    [Fact]
    public void Integrity_is_ok_only_for_the_single_ok_row()
    {
        new IntegrityCheckOutcome(["ok"], 0).IntegrityOk.Should().BeTrue();
        new IntegrityCheckOutcome(["row 2041 missing from index idx_events_ts"], 0).IntegrityOk.Should().BeFalse();
        new IntegrityCheckOutcome([], 0).IntegrityOk.Should().BeFalse();
    }
}
