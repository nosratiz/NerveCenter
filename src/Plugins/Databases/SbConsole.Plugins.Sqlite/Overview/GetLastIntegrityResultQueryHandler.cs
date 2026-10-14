using Microsoft.Extensions.DependencyInjection;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Overview;

public sealed class GetLastIntegrityResultQueryHandler(
    [FromKeyedServices(SqlitePlugin.PluginId)] IPluginStore store) : IScopedService
{
    public Task<IntegrityResult?> HandleAsync(Guid connectionId, CancellationToken ct = default) =>
        IntegrityResultStore.ReadAsync(store, connectionId, ct);
}
