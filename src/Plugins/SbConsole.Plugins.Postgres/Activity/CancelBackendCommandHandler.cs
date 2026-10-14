using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Postgres.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Postgres.Activity;

public sealed record BackendCommand(Guid ConnectionId, string ConnectionName, int Pid);

public sealed class CancelBackendCommandHandler(
    IPostgresOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<CancelBackendCommandHandler> logger) : IScopedService
{
    public const string Action = "pg.backend.cancel";

    public Task<PluginResult> HandleAsync(BackendCommand cmd, CancellationToken ct = default) =>
        BackendCommandRunner.RunAsync(operations, connections, audit, logger, cmd, Action, ActionRisk.Mutating,
            (secret, pid, token) => operations.CancelBackendAsync(secret, pid, token), ct);
}
