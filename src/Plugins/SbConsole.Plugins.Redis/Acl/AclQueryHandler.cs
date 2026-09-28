using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis.Acl;

/// <summary>
/// Backs the ACL-aware disabled-button state (design spec §12/§14): a read-only check, so it never
/// audits. Shared across pages -- the Pub/Sub page's Publish button and the Keys page's FLUSHDB
/// button both call the same HasCommandPermissionAsync("FLUSHDB"/"PUBLISH") check, matching the
/// design brief's own framing of Pub/Sub's ACL-checking precedent as something later buttons reuse.
/// </summary>
public sealed class AclQueryHandler(IRedisOperations operations, IConnectionProvider connections, ILogger<AclQueryHandler> logger)
{
    public Task<PluginResult<bool>> HasCommandPermissionAsync(Guid connectionId, string command, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "redis.acl.check",
            secret => operations.HasCommandPermissionAsync(secret, command, ct), ct);
}
