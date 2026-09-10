using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SbConsole.Core.Migrations
{
    /// <inheritdoc />
    public partial class AuditEntryAtStoredAsUtcTicks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Backfill existing rows BEFORE changing the column's declared type. The "At" column was
            // previously SQLite TEXT holding EF's DateTimeOffset format (e.g. "2026-09-09
            // 20:50:00+00:00"). The AlterColumn below only changes the declared type to INTEGER --
            // SQLite does not coerce that TEXT content into the new INTEGER affinity, so any
            // pre-existing rows would keep their raw TEXT bytes sitting in an INTEGER-typed column.
            // Read back afterward through SbcDbContext's UTC-ticks value converter, that garbage
            // silently decodes as a bogus instant (verified: reads back as year 0001) instead of
            // throwing. julianday() parses the TEXT DateTimeOffset format, including its UTC offset
            // suffix, into a Julian day number for the represented UTC instant; the arithmetic below
            // converts that to .NET ticks (100ns units since 0001-01-01 UTC), using
            // 621355968000000000 as the ticks value at the Unix epoch. Precision note: this backfill
            // is only accurate to the millisecond, so any sub-millisecond fraction in pre-existing
            // timestamps is lost -- acceptable for an audit log.
            migrationBuilder.Sql(
                """
                UPDATE AuditEntries
                SET At = 621355968000000000 + CAST(ROUND((julianday(At) - 2440587.5) * 86400.0 * 1000) AS INTEGER) * 10000;
                """);

            migrationBuilder.AlterColumn<long>(
                name: "At",
                table: "AuditEntries",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "TEXT");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "At",
                table: "AuditEntries",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "INTEGER");

            // Mirror-image backfill of the Up() conversion above, so Down() undoes Up() fully (data
            // included), not just the schema. NOTE ON ORDERING: even though this Sql() call appears
            // after the AlterColumn above in source, EF Core's SQLite migrations generator does not
            // run it after the column is back to TEXT. SQLite can't alter a column type in place, so
            // AlterColumn is realized as a table rebuild (CREATE a temp table with the new schema,
            // INSERT ... SELECT the old rows into it, DROP the original, RENAME the temp table into
            // place); that rebuild is deferred and only materializes once enough pending operations
            // force it, and this Sql() operation runs BEFORE that rebuild completes, against the
            // still-INTEGER-typed "AuditEntries" table (verified via `dotnet ef migrations script`
            // for the down direction: the UPDATE below precedes the CREATE ef_temp_AuditEntries /
            // INSERT / DROP / RENAME sequence, and EF itself warns "An operation of type
            // 'SqlOperation' will be attempted while a rebuild of table 'AuditEntries' is pending").
            // This still round-trips correctly because SQLite has dynamic typing: writing this TEXT
            // string into the (still nominally INTEGER-affinity) column succeeds as-is, and the
            // subsequent rebuild's INSERT ... SELECT copies that TEXT value straight into the new
            // TEXT-typed column. Converts UTC-ticks INTEGER values back to the TEXT DateTimeOffset
            // format EF Core's SQLite provider expects (e.g. "2026-09-09 20:50:00.000+00:00"): ticks
            // -> unix seconds via the same epoch constant used in Up(), then strftime(...,
            // 'unixepoch') renders the UTC calendar date/time with millisecond precision. "+00:00" is
            // appended because the value converter in SbcDbContext always stores these values as UTC
            // (TimeSpan.Zero offset). As in Up(), this round-trip is only accurate to the millisecond
            // -- acceptable for an audit log.
            migrationBuilder.Sql(
                """
                UPDATE AuditEntries
                SET At = strftime('%Y-%m-%d %H:%M:%f', (At - 621355968000000000) / 10000000.0, 'unixepoch') || '+00:00';
                """);
        }
    }
}
