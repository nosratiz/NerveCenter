using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Databases;

/// <summary>
/// "New database…" (design spec §13). Folder/SubFolder/FileName/Journal/Template/Script describe the
/// file; ConnectionName/Mode/Tags/BackupDir the connection saved for it.
/// </summary>
public sealed record CreateDatabaseCommand(
    string Folder,
    string? SubFolder,
    string FileName,
    NewDatabaseJournal Journal,
    NewDatabaseTemplate Template,
    string? Script,
    string ConnectionName,
    SqliteAccessMode Mode,
    IReadOnlyList<string> Tags,
    string? BackupDir);

/// <summary>The new connection and the file it points at (configured form).</summary>
public sealed record DatabaseCreated(Guid ConnectionId, string ConnectionName, string Path);

/// <summary>
/// New database (spec §13). Mutating, audited "sqlite.database.create" on the new connection's name,
/// detail "path · template · journal". Steps: (1) validate -- name not blank and not taken by a
/// SQLite connection (IConnectionProvider.ListAsync), then the operation's own folder / file / script
/// checks, all before the disk is touched; (2-3) the operation creates the file and applies the
/// template, deleting it on any failure; (4) IConnectionCreator saves the connection, and on
/// Conflict / Invalid / an exception the file is deleted again; (5) the new connection id is returned.
///
/// Audit convention (as HandlerRunner.TryResolveSecretAsync: a row only for work that ran): input
/// refused before anything touched the disk -- blank or taken name, a refused folder / file name /
/// existing file, a refused script -- is returned without an audit row. Once a file was created,
/// every outcome is audited: success, a template that failed (file deleted), or a connection that
/// couldn't be saved (file deleted). From the moment the file exists the remaining steps ignore
/// cancellation, so a cancelled request never leaves an orphan file without a connection.
/// Nothing extra is confirmed for prod-tagged new connections: nothing existing is touched.
/// </summary>
public sealed class CreateDatabaseCommandHandler(
    ISqliteOperations operations,
    IConnectionProvider connections,
    IConnectionCreator creator,
    IAuditScope audit,
    ILogger<CreateDatabaseCommandHandler> logger) : IScopedService
{
    public const string Action = "sqlite.database.create";

    public async Task<PluginResult<DatabaseCreated>> HandleAsync(CreateDatabaseCommand cmd, CancellationToken ct = default)
    {
        var name = cmd.ConnectionName?.Trim() ?? "";
        if (name.Length == 0)
        {
            return PluginResult<DatabaseCreated>.Fail("Type a connection name.");
        }

        var backupDir = string.IsNullOrWhiteSpace(cmd.BackupDir) ? null : cmd.BackupDir.Trim();
        if (backupDir is not null && backupDir.Contains(';'))
        {
            return PluginResult<DatabaseCreated>.Fail("The backups directory can't contain ';'.");
        }

        try
        {
            var existing = await connections.ListAsync(SqlitePlugin.Kind, ct);
            if (existing.Any(c => string.Equals(c.Name, name, StringComparison.Ordinal)))
            {
                return PluginResult<DatabaseCreated>.Fail($"A connection named '{name}' already exists — pick another name.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "{Action}: listing SQLite connections failed.", Action);
            return PluginResult<DatabaseCreated>.Fail(FriendlySqliteError.From(ex));
        }

        var template = cmd.Template.ToString().ToLowerInvariant();
        var journal = cmd.Journal.ToString().ToUpperInvariant();
        var plannedPath = PlannedPath(cmd);
        NewDatabaseCreated created;
        try
        {
            created = await operations.CreateDatabaseAsync(
                new NewDatabaseRequest(cmd.Folder, cmd.SubFolder, cmd.FileName, cmd.Journal, cmd.Template, cmd.Script), ct);
        }
        catch (Exception ex) when (ex is SqliteNewDatabaseRefusedException or SqliteScriptRefusedException)
        {
            return PluginResult<DatabaseCreated>.Fail(ex.Message); // nothing touched the disk: not audited
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "{Action} of {Path} failed.", Action, plannedPath);
            var friendly = FriendlySqliteError.From(ex);
            await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, name, ActionRisk.Mutating, succeeded: false,
                $"{plannedPath} · {template} · {journal} · {friendly}");
            return PluginResult<DatabaseCreated>.Fail(friendly);
        }

        var detail = $"{created.Path} · {template} · {journal}";
        var tags = cmd.Tags.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList();
        string? failure;
        try
        {
            var secret = new SqliteConnectionSettings(created.Path, cmd.Mode, BackupDir: backupDir).ToSecretString();
            var saved = await creator.CreateAsync(name, SqlitePlugin.Kind, secret, tags, CancellationToken.None);
            if (saved is ConnectionCreateResult.Created { Id: var id })
            {
                await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, name, ActionRisk.Mutating, succeeded: true, detail);
                return PluginResult<DatabaseCreated>.Ok(new DatabaseCreated(id, name, created.Path));
            }

            failure = saved switch
            {
                ConnectionCreateResult.Conflict => $"A connection named '{name}' already exists — the new file was removed.",
                ConnectionCreateResult.Invalid invalid => $"The connection could not be saved, so the new file was removed: {invalid.Error}",
                _ => "The connection could not be saved, so the new file was removed.",
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Action}: saving the connection for {Path} failed.", Action, created.Path);
            failure = $"The connection could not be saved, so the new file was removed: {FriendlySqliteError.From(ex)}";
        }

        await DeleteQuietlyAsync(created.Path);
        await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, name, ActionRisk.Mutating, succeeded: false, $"{detail} · {failure}");
        return PluginResult<DatabaseCreated>.Fail(failure);
    }

    private static string PlannedPath(CreateDatabaseCommand cmd)
    {
        var fileName = NewDatabaseRules.FileNameError(cmd.FileName) is null ? NewDatabaseRules.WithExtension(cmd.FileName) : cmd.FileName?.Trim() ?? "";
        var sub = cmd.SubFolder?.Trim().Trim('/') ?? "";
        return sub.Length == 0 ? Path.Combine(cmd.Folder, fileName) : Path.Combine(cmd.Folder, sub, fileName);
    }

    private async Task DeleteQuietlyAsync(string path)
    {
        try
        {
            await operations.DeleteCreatedDatabaseAsync(path, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Action}: removing {Path} after the connection could not be saved failed.", Action, path);
        }
    }
}
