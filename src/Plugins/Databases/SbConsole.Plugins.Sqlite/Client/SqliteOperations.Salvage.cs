using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SQLitePCL;

namespace SbConsole.Plugins.Sqlite.Client;

/// <summary>
/// "Recover into new file" (mockup 1g, design spec §11.6). The bundled build has no sqlite3_recover,
/// so this is a best-effort salvage: the live file is opened read-only (plugin-owned SQL, backup
/// timeout), and a new file -- the only file the plugin creates besides backups -- is written inside
/// the policy-checked BackupDir. Tables first (DDL, then rows one by one, skipping rows and tables
/// that error), then indexes, views and triggers. A fatal error (timeout, disk full, cancel) deletes
/// the partial file.
/// </summary>
public sealed partial class SqliteOperations
{
    private const int SalvageCancelCheckRows = 1000;

    public Task<SalvageOutcome> SalvageAsync(string secret, DateTimeOffset nowUtc, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var started = Stopwatch.GetTimestamp();
            var settings = SqliteConnectionSettings.Parse(secret);
            // Re-read from the decrypted secret (§11.1): recover is offered for read-write connections only.
            if (settings.Mode != SqliteAccessMode.ReadWrite)
            {
                throw new SqliteReadOnlyConnectionException();
            }

            var live = ResolveLivePath(settings);
            var dir = ResolveBackupDir(settings, mustExist: true)!;
            var name = FreeName(dir, $"{Path.GetFileNameWithoutExtension(live)}-recovered-{Stamp(nowUtc)}", ".db");
            var target = ResolveInside(dir, name);
            try
            {
                var outcome = Run(secret, SqliteOpenOptions.Backup, source => Salvage(source, target, ct), ct);
                _logger.LogInformation("Salvaged {Live} into {Target}: {Tables} tables, {Rows} rows, {Failed} failures.",
                    live, target, outcome.TablesCopied, outcome.RowsCopied, outcome.Failures.Count);
                return outcome with { FileName = name, Duration = Stopwatch.GetElapsedTime(started) };
            }
            catch
            {
                SqliteConnection.ClearAllPools();
                DeleteQuietly(target);
                DeleteQuietly(target + "-journal");
                throw;
            }
        }, ct);

    private SalvageOutcome Salvage(SqliteConnection source, string targetPath, CancellationToken ct)
    {
        // rowid order = creation order, so a virtual table comes before the shadow tables it creates.
        var objects = SqliteSql.Rows(source,
            @"SELECT type, name, sql FROM sqlite_schema WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite\_%' ESCAPE '\' ORDER BY rowid",
            r => (Type: r.GetString(0), Name: r.GetString(1), Sql: r.GetString(2)));

        using var target = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = targetPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        target.Open();
        // The DDL replayed below is the file's own schema text: never let it reach another file.
        raw.sqlite3_limit(target.Handle, raw.SQLITE_LIMIT_ATTACHED, 0);
        // Rows arrive table by table, so references can't be satisfied in order; nothing in the copy is enforced.
        SqliteSql.Execute(target, "PRAGMA foreign_keys = OFF");
        SqliteSql.Execute(target, "PRAGMA trusted_schema = OFF");

        var failures = new List<SalvageFailure>();
        var tablesCopied = 0;
        long rowsCopied = 0;
        long rowsSkipped = 0;
        var tables = objects.Where(o => o.Type == "table")
            .OrderBy(o => IsVirtual(o.Sql) ? 0 : 1) // stable: creation order within each group
            .ToList();
        foreach (var table in tables)
        {
            ct.ThrowIfCancellationRequested();
            if (SqliteSql.Long(target, "SELECT COUNT(*) FROM sqlite_schema WHERE name = $name", ("$name", table.Name)) > 0)
            {
                continue; // a shadow table its virtual table already created (and fills)
            }

            try
            {
                SqliteSql.Execute(target, table.Sql);
            }
            catch (Exception ex) when (IsSalvageable(ex))
            {
                failures.Add(new SalvageFailure("table", table.Name, FriendlySqliteError.From(ex)));
                continue;
            }

            var (copied, skipped, error) = CopyRows(source, target, table.Name, ct);
            rowsCopied += copied;
            rowsSkipped += skipped;
            if (error is null)
            {
                tablesCopied++;
            }
            else
            {
                failures.Add(new SalvageFailure("table", table.Name, $"stopped after {SqliteFormat.Count(copied)} rows — {error}"));
            }
        }

        var others = 0;
        foreach (var type in new[] { "index", "view", "trigger" })
        {
            foreach (var obj in objects.Where(o => o.Type == type))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    SqliteSql.Execute(target, obj.Sql);
                    others++;
                }
                catch (Exception ex) when (IsSalvageable(ex))
                {
                    failures.Add(new SalvageFailure(type, obj.Name, FriendlySqliteError.From(ex)));
                }
            }
        }

        return new SalvageOutcome("", tablesCopied, rowsCopied, rowsSkipped, others, failures, TimeSpan.Zero);
    }

    /// <summary>Copies the table's stored (non-generated, non-hidden) columns row by row, in one target transaction.</summary>
    private static (long Copied, long Skipped, string? Error) CopyRows(SqliteConnection source, SqliteConnection target, string table, CancellationToken ct)
    {
        long copied = 0;
        long skipped = 0;
        string? error = null;
        SqliteSql.Execute(target, "BEGIN");
        try
        {
            var columns = SqliteSql.Rows(source, "SELECT name FROM pragma_table_xinfo($name) WHERE hidden = 0 ORDER BY cid",
                r => SqliteSql.QuoteIdentifier(r.GetString(0)), ("$name", table));
            if (columns.Count == 0)
            {
                SqliteSql.Execute(target, "COMMIT");
                return (0, 0, null);
            }

            var quoted = SqliteSql.QuoteIdentifier(table);
            using var insert = target.CreateCommand();
            insert.CommandText = string.Create(CultureInfo.InvariantCulture,
                $"INSERT INTO {quoted} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", columns.Select((_, i) => $"$p{i}"))})");
            var parameters = columns.Select((_, i) => insert.Parameters.Add(new SqliteParameter($"$p{i}", DBNull.Value))).ToArray();

            using var select = source.CreateCommand();
            select.CommandText = $"SELECT {string.Join(", ", columns)} FROM {quoted}";
            try
            {
                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    if ((copied + skipped) % SalvageCancelCheckRows == 0)
                    {
                        ct.ThrowIfCancellationRequested();
                    }

                    try
                    {
                        for (var i = 0; i < parameters.Length; i++)
                        {
                            parameters[i].Value = reader.GetValue(i);
                        }

                        insert.ExecuteNonQuery();
                        copied++;
                    }
                    catch (Exception ex) when (IsSalvageable(ex))
                    {
                        skipped++;
                    }
                }
            }
            catch (Exception ex) when (IsSalvageable(ex))
            {
                // Reading stopped part-way (typically a corrupt page): keep what was read.
                error = FriendlySqliteError.From(ex);
            }

            SqliteSql.Execute(target, "COMMIT");
            return (copied, skipped, error);
        }
        catch
        {
            if (raw.sqlite3_get_autocommit(target.Handle) == 0)
            {
                SqliteSql.Execute(target, "ROLLBACK");
            }

            throw;
        }
    }

    private static bool IsVirtual(string sql) => sql.TrimStart().StartsWith("CREATE VIRTUAL TABLE", StringComparison.OrdinalIgnoreCase);

    /// <summary>Per-object / per-row failures are skipped; interrupts (timeout), a full disk and cancellation end the salvage.</summary>
    private static bool IsSalvageable(Exception ex) => ex switch
    {
        OperationCanceledException => false,
        SqliteException { SqliteErrorCode: var code } => code != raw.SQLITE_INTERRUPT && code != raw.SQLITE_FULL,
        _ => ex is InvalidCastException or FormatException or ArgumentException or System.Text.DecoderFallbackException,
    };
}
