using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Query;

public sealed record DeleteSavedQueryCommand(Guid Id);

/// <summary>Removes a saved query; already gone is success. Store-only and not audited (user preference data, see SaveQueryCommandHandler).</summary>
public sealed class DeleteSavedQueryCommandHandler(
    [FromKeyedServices(SqlitePlugin.PluginId)] IPluginStore store, ILogger<DeleteSavedQueryCommandHandler> logger) : IScopedService
{
    public async Task<PluginResult> HandleAsync(DeleteSavedQueryCommand cmd, CancellationToken ct = default)
    {
        try
        {
            var queries = (await SavedQueriesStore.ReadAsync(store, ct)).ToList();
            if (queries.RemoveAll(q => q.Id == cmd.Id) > 0)
            {
                await SavedQueriesStore.WriteAsync(store, queries, ct);
            }

            return PluginResult.Ok();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Deleting the saved SQLite query {Id} failed.", cmd.Id);
            return PluginResult.Fail(ex);
        }
    }
}
