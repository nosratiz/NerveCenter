using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Backups;

/// <summary>The Backups page tiles' schedule numbers (design spec §11.7), from BackupSchedule.</summary>
public sealed record BackupScheduleStatus(
    BackupRecord? LastSuccess,
    double? HoursSinceLastSuccess,
    bool Overdue,
    bool Due,
    DateTimeOffset? NextSlot,
    int MissedSlots);

/// <summary>
/// Everything the Backups page shows in one load. BackupDir is the secret's setting as typed (null =
/// backups not enabled). Policy is the saved policy or the defaults (PolicySaved false). Live is the
/// live file's fingerprint for the comparison panel -- null with LiveError when it couldn't be read,
/// so a broken live file still shows its history. PrunePreview = what the next prune removes.
/// </summary>
public sealed record BackupsOverview(
    string? BackupDir,
    IReadOnlyList<BackupRecord> History,
    BackupPolicy Policy,
    bool PolicySaved,
    SqliteFileFingerprint? Live,
    string? LiveError,
    IReadOnlyList<BackupRecord> PrunePreview,
    BackupScheduleStatus Schedule)
{
    /// <summary>Successful records with a file -- "N kept".</summary>
    public int Kept => History.Count(r => r.Succeeded && r.FileName is not null);

    public long TotalBytes => History.Where(r => r.Succeeded && r.FileName is not null).Sum(r => r.Bytes);

    public int VerifiedOk => History.Count(r => r.Succeeded && r.Verify == BackupRecord.VerifyOk);

    public long PruneBytes => PrunePreview.Where(r => r.FileName is not null).Sum(r => r.Bytes);
}

/// <summary>Backups page load (design spec §11.7). A query: reads only, never audited.</summary>
public sealed class GetBackupsQueryHandler(
    ISqliteOperations operations,
    IConnectionProvider connections,
    [FromKeyedServices(SqlitePlugin.PluginId)] IPluginStore store,
    TimeProvider clock,
    ILogger<GetBackupsQueryHandler> logger) : IScopedService
{
    public Task<PluginResult<BackupsOverview>> HandleAsync(Guid connectionId, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "sqlite.backups.get", async secret =>
        {
            var settings = SqliteConnectionSettings.Parse(secret);
            var history = await BackupHistoryStore.ReadAsync(store, connectionId, ct);
            var saved = await BackupPolicyStore.ReadAsync(store, connectionId, ct);
            var policy = saved ?? BackupPolicy.Default;
            var now = clock.GetUtcNow();

            SqliteFileFingerprint? live = null;
            string? liveError = null;
            try
            {
                live = await operations.GetLiveFingerprintAsync(secret, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Reading the live fingerprint for connection {ConnectionId} failed.", connectionId);
                liveError = FriendlySqliteError.From(ex);
            }

            var schedule = new BackupScheduleStatus(
                BackupSchedule.LastSuccess(history),
                BackupSchedule.HoursSinceLastSuccess(history, now),
                BackupSchedule.IsOverdue(saved, history, now),
                BackupSchedule.IsDue(saved, history, now),
                BackupSchedule.NextSlot(saved, now),
                BackupSchedule.MissedSlots(saved, history, now));
            return new BackupsOverview(settings.BackupDir, history, policy, saved is not null, live, liveError,
                BackupRetention.Plan(history, policy, now), schedule);
        }, ct);
}
