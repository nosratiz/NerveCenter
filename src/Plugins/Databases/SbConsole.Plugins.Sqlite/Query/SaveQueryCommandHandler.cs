using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Query;

public sealed record SaveQueryCommand(string Name, string Sql);

/// <summary>
/// Saves (or, under an existing name, case-insensitive, replaces) a saved query. Store-only and not
/// audited: a saved query is user preference data -- running it is what gets audited.
/// </summary>
public sealed class SaveQueryCommandHandler(
    [FromKeyedServices(SqlitePlugin.PluginId)] IPluginStore store, TimeProvider clock, ILogger<SaveQueryCommandHandler> logger) : IScopedService
{
    public async Task<PluginResult<SavedQuery>> HandleAsync(SaveQueryCommand cmd, CancellationToken ct = default)
    {
        var name = cmd.Name.Trim();
        if (name.Length == 0)
        {
            return PluginResult<SavedQuery>.Fail("Give the query a name.");
        }

        if (name.Length > SavedQuery.MaxNameLength)
        {
            return PluginResult<SavedQuery>.Fail($"Names are at most {SavedQuery.MaxNameLength} characters.");
        }

        if (string.IsNullOrWhiteSpace(cmd.Sql))
        {
            return PluginResult<SavedQuery>.Fail("There is no SQL to save.");
        }

        try
        {
            var queries = (await SavedQueriesStore.ReadAsync(store, ct)).ToList();
            var existing = queries.FirstOrDefault(q => string.Equals(q.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing is null && queries.Count >= SavedQuery.MaxSaved)
            {
                return PluginResult<SavedQuery>.Fail($"At most {SavedQuery.MaxSaved} saved queries — delete some first.");
            }

            var saved = new SavedQuery(existing?.Id ?? Guid.NewGuid(), name, cmd.Sql, clock.GetUtcNow());
            queries.RemoveAll(q => q.Id == saved.Id);
            queries.Insert(0, saved);
            await SavedQueriesStore.WriteAsync(store, queries, ct);
            return PluginResult<SavedQuery>.Ok(saved);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Saving the SQLite query {Name} failed.", name);
            return PluginResult<SavedQuery>.Fail(ex);
        }
    }
}
