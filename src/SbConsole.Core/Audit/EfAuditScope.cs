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
    AuthenticationStateProvider authStateProvider) : IAuditScope, IScopedService
{
    public const string SystemActor = "system";

    public async Task RecordAsync(string action, string target, ActionRisk risk, bool succeeded, string? detail = null, CancellationToken ct = default)
    {
        var actor = await ResolveActorAsync();

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

    /// <summary>
    /// The signed-in user's name ("admin" if the identity has none). Outside a Blazor circuit -- a
    /// hosted service's scope, e.g. the SQLite backup scheduler -- the server provider has no state
    /// and throws InvalidOperationException; that work is recorded as "system" (design spec §11.7).
    /// </summary>
    private async Task<string> ResolveActorAsync()
    {
        try
        {
            var state = await authStateProvider.GetAuthenticationStateAsync();
            return state.User.Identity?.Name ?? "admin";
        }
        catch (InvalidOperationException)
        {
            return SystemActor;
        }
    }
}
