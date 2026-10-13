using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Backups;

/// <summary>
/// Scheduled backups and growth samples (design spec §11.7, §11.8). Ticks every minute on
/// TimeProvider; each tick gets its own DI scope (the handlers are scoped) and walks every SQLite
/// connection: a growth sample at most once an hour, then -- with a backup directory and an enabled
/// policy whose slot is due -- back up (trigger "schedule", actor "system"), verify, prune. The
/// policy is the consent, so the prune isn't confirmed. Every connection is its own try/catch, and a
/// failed tick never stops later ticks; a failed backup is recorded in history by the handler.
/// Same shape as the host's MetricsCollectorService. Hand-registered (AddHostedService) in
/// SqlitePlugin.ConfigureServices.
/// </summary>
public sealed class SqliteBackupScheduler(
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    ILogger<SqliteBackupScheduler> logger) : BackgroundService
{
    public const string SystemActor = "system";

    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "The SQLite backup tick failed; trying again next tick.");
            }

            try
            {
                await Task.Delay(TickInterval, clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    // Public so tests can run one tick directly instead of waiting on the timer (MetricsCollectorService convention).
    public async Task TickAsync(CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var connections = services.GetRequiredService<IConnectionProvider>();
        var store = services.GetRequiredService<IPluginStoreFactory>().For(SqlitePlugin.PluginId);
        var operations = services.GetRequiredService<ISqliteOperations>();
        var now = clock.GetUtcNow();

        foreach (var connection in await connections.ListAsync(SqlitePlugin.Kind, ct))
        {
            try
            {
                await ProcessAsync(services, connections, operations, store, connection, now, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Scheduled SQLite work for connection {ConnectionId} failed; skipping it this tick.", connection.Id);
            }
        }
    }

    private async Task ProcessAsync(
        IServiceProvider services, IConnectionProvider connections, ISqliteOperations operations, IPluginStore store,
        ConnectionInfo connection, DateTimeOffset now, CancellationToken ct)
    {
        // Inside the per-connection try: a decryption failure must not stop the other connections.
        var secret = await connections.GetSecretAsync(connection.Id, ct);
        if (secret is null)
        {
            return;
        }

        try
        {
            await GrowthSampler.SampleIfDueAsync(operations, store, connection.Id, secret, now, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // A growth sample failing (file gone, path refused) must not stop the backup.
            logger.LogDebug(ex, "Growth sample for connection {ConnectionId} failed.", connection.Id);
        }

        if (SqliteConnectionSettings.Parse(secret).BackupDir is null)
        {
            return;
        }

        var policy = await BackupPolicyStore.ReadAsync(store, connection.Id, ct);
        var history = await BackupHistoryStore.ReadAsync(store, connection.Id, ct);
        if (!BackupSchedule.IsDue(policy, history, now))
        {
            return;
        }

        logger.LogInformation("Scheduled backup of {Connection} is due.", connection.Name);
        var backup = await services.GetRequiredService<RunBackupCommandHandler>().HandleAsync(
            new RunBackupCommand(connection.Id, connection.Name, SystemActor, BackupRecord.TriggerSchedule), ct);
        if (!backup.IsSuccess)
        {
            logger.LogWarning("Scheduled backup of {Connection} failed: {Error}", connection.Name, backup.Error);
            return;
        }

        var prune = await services.GetRequiredService<PruneBackupsCommandHandler>().HandleAsync(new PruneBackupsCommand(connection.Id, connection.Name), ct);
        if (!prune.IsSuccess || prune.Value!.Failed > 0)
        {
            logger.LogWarning("Pruning backups of {Connection} after the scheduled backup did not fully succeed: {Error}", connection.Name, prune.Error);
        }
    }
}
