using Microsoft.Extensions.DependencyInjection;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Query;

public sealed class ListSavedQueriesQueryHandler(
    [FromKeyedServices(SqlitePlugin.PluginId)] IPluginStore store) : IScopedService
{
    public Task<IReadOnlyList<SavedQuery>> HandleAsync(CancellationToken ct = default) => SavedQueriesStore.ReadAsync(store, ct);
}
