using Microsoft.AspNetCore.Components.Authorization;

namespace SbConsole.Web.Auth;

/// <summary>
/// Resolves the acting user for audit purposes from the Blazor authentication-state cascade.
/// Shared by components that call a command handler, so they don't each reimplement the same
/// fallback logic (see Plan 2 Task 6's "Actor resolution" note).
/// </summary>
public static class ActorResolver
{
    /// <param name="authState">
    /// The component's cascaded <c>Task&lt;AuthenticationState&gt;?</c>. A null value covers bUnit
    /// test hosts that don't supply the cascade; the real app always has it via
    /// <c>AddCascadingAuthenticationState()</c>.
    /// </param>
    public static async Task<string> ResolveAsync(Task<AuthenticationState>? authState) =>
        authState is null ? "admin" : (await authState).User.Identity?.Name ?? "admin";
}
