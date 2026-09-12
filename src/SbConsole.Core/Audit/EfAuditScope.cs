using Microsoft.AspNetCore.Components.Authorization;
using SbConsole.Core.Data.Entities;
using SbConsole.Sdk;

namespace SbConsole.Core.Audit;

/// <summary>
/// Host implementation of the Sdk's IAuditScope. Injected into plugin handler classes (plain
/// classes, not components), so — unlike the Razor-component-facing ActorResolver, which reads
/// the AuthenticationState cascading parameter it's handed — this resolves the actor itself via
/// AuthenticationStateProvider.
/// </summary>
public sealed class EfAuditScope(
    IAuditWriter writer,
    TimeProvider clock,
    AuthenticationStateProvider authStateProvider) : IAuditScope
{
    public async Task RecordAsync(string action, string target, ActionRisk risk, bool succeeded, string? detail = null, CancellationToken ct = default)
    {
        var state = await authStateProvider.GetAuthenticationStateAsync();
        var actor = state.User.Identity?.Name ?? "admin";

        await writer.WriteAsync(new AuditEntry
        {
            At = clock.GetUtcNow(),
            Actor = actor,
            Action = action,
            Target = target,
            Risk = risk,
            Succeeded = succeeded,
            Detail = detail,
        }, ct);
    }
}
