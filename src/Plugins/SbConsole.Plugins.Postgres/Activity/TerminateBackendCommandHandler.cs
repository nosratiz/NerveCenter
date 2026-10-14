using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Postgres.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Postgres.Activity;

public sealed class TerminateBackendCommandHandler(
    IPostgresOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<TerminateBackendCommandHandler> logger) : IScopedService
{
    public const string Action = "pg.backend.terminate";

    public Task<PluginResult> HandleAsync(BackendCommand cmd, CancellationToken ct = default) =>
        BackendCommandRunner.RunAsync(operations, connections, audit, logger, cmd, Action, ActionRisk.Destructive,
            (secret, pid, token) => operations.TerminateBackendAsync(secret, pid, token), ct);
}
