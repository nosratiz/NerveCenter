using System.Globalization;

namespace SbConsole.Plugins.Sqlite.Client;

public sealed partial class SqliteOperations
{
    public Task<IntegrityCheckOutcome> RunIntegrityCheckAsync(string secret, CancellationToken ct = default) =>
        RunAsync(secret, c => new IntegrityCheckOutcome(
            SqliteSql.Rows(c, $"PRAGMA integrity_check({IntegrityCheckOutcome.MaxMessages.ToString(CultureInfo.InvariantCulture)})", r => r.GetString(0)),
            SqliteSql.Rows(c, "PRAGMA foreign_key_check", _ => 0).Count), ct);
}
