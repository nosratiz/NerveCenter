namespace SbConsole.Sdk;

/// <summary>
/// Triggers the host's confirmation dialog for a mutating or destructive action.
/// Whether typed confirmation (type the target name) is required — versus a plain
/// two-button confirm — is decided by the implementation from the target's prod tag
/// and the host's Settings toggle; callers only describe what they're about to do.
/// </summary>
public interface IConfirmationService
{
    Task<bool> ConfirmAsync(string verb, string target, bool isProd, int? count = null, CancellationToken ct = default);
}
