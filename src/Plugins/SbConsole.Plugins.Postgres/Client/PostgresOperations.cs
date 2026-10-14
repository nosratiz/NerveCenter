using System.Data;
using Microsoft.Extensions.Logging;
using Npgsql;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Postgres.Client;

/// <summary>
/// Npgsql implementation. Every call parses the secret, opens a fresh connection (the driver pools them) and
/// runs its reads inside a READ ONLY transaction with a server-side statement timeout.
/// </summary>
public sealed partial class PostgresOperations(ILogger<PostgresOperations> logger) : IPostgresOperations, ISingletonService
{
    private static NpgsqlConnection Open(PostgresConnectionSettings s, string? db) =>
        new(s.BuildConnectionString(db));

    private async Task<T> ReadAsync<T>(
        string secret, string? db, Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> work,
        CancellationToken ct, int? timeoutS = null)
    {
        var settings = PostgresConnectionSettings.Parse(secret);
        await using var conn = Open(settings, db);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            // The timeout is an int from settings (or the caller), formatted here -- never user text.
            var seconds = Math.Clamp(timeoutS ?? settings.StatementTimeoutS, 1, PostgresConnectionSettings.MaxTimeoutS);
            await using (var cmd = new NpgsqlCommand(
                $"SET TRANSACTION READ ONLY; SET LOCAL statement_timeout = '{seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}s'",
                conn, tx))
            {
                await cmd.ExecuteNonQueryAsync(ct);
            }

            return await work(conn, tx);
        }
        finally
        {
            try
            {
                await tx.RollbackAsync(CancellationToken.None);
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or ObjectDisposedException)
            {
                // The connection is already broken; disposal discards it.
            }
        }
    }

    public async Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default)
    {
        try
        {
            var (version, replica) = await ReadAsync(secret, null, async (conn, tx) =>
            {
                await using var cmd = new NpgsqlCommand("SELECT current_setting('server_version'), pg_is_in_recovery()", conn, tx);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                await r.ReadAsync(ct);
                return (r.GetString(0), r.GetBoolean(1));
            }, ct);
            return new ConnectionTestResult(true, null, $"PostgreSQL {version} · {(replica ? "replica" : "primary")}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "PostgreSQL connection test failed");
            return new ConnectionTestResult(false, FriendlyPostgresError.From(ex));
        }
    }

    public Task<IReadOnlyList<string>> ListDatabasesAsync(string secret, CancellationToken ct = default) =>
        ReadAsync<IReadOnlyList<string>>(secret, null, async (conn, tx) =>
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT datname FROM pg_database WHERE datallowconn AND NOT datistemplate ORDER BY datname", conn, tx);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var names = new List<string>();
            while (await r.ReadAsync(ct))
            {
                names.Add(r.GetString(0));
            }

            return names;
        }, ct);

    // Stubs: later tasks move each of these into its own partial file (Schema, Rows, Query, Activity, Maintenance, Export).
    public Task<PgSchemaTree> ListSchemaAsync(string secret, string? database, CancellationToken ct = default) => throw new NotImplementedException("Task 5");
    public Task<PgObjectDetail?> GetObjectDetailAsync(string secret, string? database, uint oid, CancellationToken ct = default) => throw new NotImplementedException("Task 5");
    public Task<string?> GetDdlAsync(string secret, string? database, uint oid, CancellationToken ct = default) => throw new NotImplementedException("Task 5");
    public Task<PgRowPage> PreviewRowsAsync(string secret, string? database, PgRowsRequest request, CancellationToken ct = default) => throw new NotImplementedException("Task 6");
    public Task<CsvExport> ExportTableCsvAsync(string secret, string? database, PgRowsRequest request, CancellationToken ct = default) => throw new NotImplementedException("Task 6");
    public Task<string> ReindexIndexAsync(string secret, string? database, uint indexOid, CancellationToken ct = default) => throw new NotImplementedException("Task 7");
    public Task<string> DropIndexAsync(string secret, string? database, uint indexOid, CancellationToken ct = default) => throw new NotImplementedException("Task 7");
    public Task<PgQueryResult> RunQueryAsync(string secret, string? database, PgQueryRequest request, CancellationToken ct = default) => throw new NotImplementedException("Task 9");
    public Task<string> ExplainAsync(string secret, string? database, string sql, int timeoutS, CancellationToken ct = default) => throw new NotImplementedException("Task 9");
    public Task<CsvExport> ExportQueryCsvAsync(string secret, string? database, string sql, int timeoutS, CancellationToken ct = default) => throw new NotImplementedException("Task 9");
    public Task<PgActivity> GetActivityAsync(string secret, CancellationToken ct = default) => throw new NotImplementedException("Task 10");
    public Task<PgBackend?> GetBackendAsync(string secret, int pid, CancellationToken ct = default) => throw new NotImplementedException("Task 10");
    public Task<bool> CancelBackendAsync(string secret, int pid, CancellationToken ct = default) => throw new NotImplementedException("Task 10");
    public Task<bool> TerminateBackendAsync(string secret, int pid, CancellationToken ct = default) => throw new NotImplementedException("Task 10");
    public Task<PgMaintenance> GetMaintenanceAsync(string secret, string? database, CancellationToken ct = default) => throw new NotImplementedException("Task 11");
    public Task<string> RunMaintenanceAsync(string secret, string? database, uint tableOid, PgMaintenanceKind kind, CancellationToken ct = default) => throw new NotImplementedException("Task 11");
}
