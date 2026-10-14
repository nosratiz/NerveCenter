using System.Globalization;
using Microsoft.Data.Sqlite;

namespace SbConsole.Plugins.Sqlite.Client;

public sealed partial class SqliteOperations
{
    public Task<IntegrityCheckOutcome> RunIntegrityCheckAsync(string secret, CancellationToken ct = default) =>
        RunAsync(secret, ReadIntegrity, ct);

    /// <summary>REINDEX rebuilds every index from its table (spec §11.6): it fixes index-only errors, never page errors.</summary>
    public Task<IntegrityCheckOutcome> ReindexAsync(string secret, CancellationToken ct = default) =>
        RunAsync(secret, SqliteOpenOptions.PluginWrite, c =>
        {
            SqliteSql.Execute(c, "REINDEX");
            return ReadIntegrity(c);
        }, ct);

    private static IntegrityCheckOutcome ReadIntegrity(SqliteConnection c) => new(
        SqliteSql.Rows(c, $"PRAGMA integrity_check({IntegrityCheckOutcome.MaxMessages.ToString(CultureInfo.InvariantCulture)})", r => r.GetString(0)),
        SqliteSql.Rows(c, "PRAGMA foreign_key_check", _ => 0).Count);
}
