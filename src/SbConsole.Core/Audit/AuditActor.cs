using Microsoft.AspNetCore.Components.Authorization;

namespace SbConsole.Core.Audit;

/// <summary>
/// The actor for host code that runs inside plugin handlers (EfAuditScope, ConnectionCreator): the
/// signed-in user's name ("admin" if the identity has none). Outside a Blazor circuit -- a hosted
/// service's scope, e.g. the SQLite backup scheduler -- the server provider has no state and
/// throws InvalidOperationException; that work is recorded as "system" (design spec §11.7).
/// </summary>
internal static class AuditActor
{
    public const string System = "system";

    public static async Task<string> ResolveAsync(AuthenticationStateProvider authStateProvider)
    {
        try
        {
            var state = await authStateProvider.GetAuthenticationStateAsync();
            return state.User.Identity?.Name ?? "admin";
        }
        catch (InvalidOperationException)
        {
            return System;
        }
    }
}
