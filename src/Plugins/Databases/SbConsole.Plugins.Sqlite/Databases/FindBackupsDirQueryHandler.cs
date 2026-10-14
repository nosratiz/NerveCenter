using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Databases;

/// <summary>
/// The New database dialog's backups offer (spec §13): `&lt;folder&gt;[/&lt;sub-folder&gt;]/backups` when
/// that directory exists and the path policy allows it, else null. Any failure just offers nothing.
/// </summary>
public sealed class FindBackupsDirQueryHandler(ISqliteOperations operations, ILogger<FindBackupsDirQueryHandler> logger) : IScopedService
{
    public async Task<string?> HandleAsync(string folder, string? subFolder, CancellationToken ct = default)
    {
        try
        {
            var dir = await operations.FindBackupsDirAsync(folder, subFolder, ct);
            return string.IsNullOrWhiteSpace(dir) ? null : dir;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Looking for a backups directory in {Folder} / {SubFolder} failed.", folder, subFolder);
            return null;
        }
    }
}
