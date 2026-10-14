using Microsoft.AspNetCore.Components.Authorization;
using SbConsole.Core.Audit;
using SbConsole.Core.Results;
using SbConsole.Sdk;

namespace SbConsole.Core.Connections;

/// <summary>
/// Host implementation of the Sdk's IConnectionCreator (SQLite plugin design spec §13). Delegates to
/// <see cref="CreateConnectionCommandHandler"/> -- the Connections page's own path -- so the secret
/// is encrypted, the plugin summary stored and a "connection.create" audit row written, with the
/// signed-in user as actor (resolved the same way as EfAuditScope, since plugin handlers have no
/// cascading auth state). Refuses a kind no registered plugin handles.
/// </summary>
public sealed class ConnectionCreator(
    CreateConnectionCommandHandler create,
    IEnumerable<IPlugin> plugins,
    AuthenticationStateProvider authStateProvider) : IConnectionCreator, IScopedService
{
    public async Task<ConnectionCreateResult> CreateAsync(
        string name, string kind, string secret, IReadOnlyList<string> tags, CancellationToken ct = default)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            return new ConnectionCreateResult.Invalid("A connection name is required.");
        }

        if (string.IsNullOrWhiteSpace(secret))
        {
            return new ConnectionCreateResult.Invalid("The connection settings are empty.");
        }

        if (!plugins.Any(p => string.Equals(p.ConnectionKind, kind, StringComparison.Ordinal)))
        {
            return new ConnectionCreateResult.Invalid($"No plugin handles connection kind '{kind}'.");
        }

        var cleanTags = (tags ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var actor = await AuditActor.ResolveAsync(authStateProvider);
        var result = await create.HandleAsync(new CreateConnectionCommand(trimmed, kind, secret, cleanTags, actor), ct);
        if (result.IsSuccess)
        {
            return new ConnectionCreateResult.Created(result.Value);
        }

        return result.Error!.Category == ErrorCategory.Conflict
            ? new ConnectionCreateResult.Conflict()
            : new ConnectionCreateResult.Invalid(result.Error.Message);
    }
}
