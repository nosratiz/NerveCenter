using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Client;

/// <summary>
/// The thin wrapper every SQLite page and handler goes through (CLAUDE.md "every plugin operation
/// goes through a thin wrapper interface"), so handler/page tests substitute it directly.
/// </summary>
public interface ISqliteOperations
{
    Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default);

    Task<SqliteOverview> GetOverviewAsync(string secret, CancellationToken ct = default);

    Task<TableSizes> GetTableSizesAsync(string secret, CancellationToken ct = default);

    Task<IntegrityCheckOutcome> RunIntegrityCheckAsync(string secret, CancellationToken ct = default);

    Task<SchemaListing> ListSchemaAsync(string secret, CancellationToken ct = default);

    Task<TableDetail> GetTableDetailAsync(string secret, string name, CancellationToken ct = default);
}
