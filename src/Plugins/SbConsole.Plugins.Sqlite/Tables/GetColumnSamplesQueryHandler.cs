using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tables;

public sealed class GetColumnSamplesQueryHandler(
    ISqliteOperations operations, IConnectionProvider connections, ILogger<GetColumnSamplesQueryHandler> logger) : IScopedService
{
    public Task<PluginResult<IReadOnlyList<ColumnSample>>> HandleAsync(Guid connectionId, string table, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "sqlite.table.samples",
            secret => operations.SampleColumnsAsync(secret, table, ColumnSample.DefaultSampleRows, ct), ct);
}
