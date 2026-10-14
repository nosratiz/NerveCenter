using Microsoft.Extensions.DependencyInjection;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Postgres.Query;

public sealed class ListQueryHistoryQueryHandler([FromKeyedServices(PostgresPlugin.PluginId)] IPluginStore store) : IScopedService
{
    public Task<IReadOnlyList<QueryHistoryEntry>> HandleAsync(Guid connectionId, CancellationToken ct = default) =>
        QueryHistoryStore.ReadAsync(store, connectionId, ct);
}
