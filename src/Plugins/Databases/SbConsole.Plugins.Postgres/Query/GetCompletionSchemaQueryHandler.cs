using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Postgres.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Postgres.Query;

/// <summary>The Query editor's completion schema (design spec §16) for one database. Not audited.</summary>
public sealed class GetCompletionSchemaQueryHandler(
    IPostgresOperations operations, IConnectionProvider connections, ILogger<GetCompletionSchemaQueryHandler> logger) : IScopedService
{
    public Task<PluginResult<PgCompletionSchema>> HandleAsync(Guid connectionId, string? database, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "postgres.query.completion-schema",
            secret => operations.GetCompletionSchemaAsync(secret, database, ct), ct);
}
