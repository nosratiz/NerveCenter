namespace SbConsole.Sdk;

/// <summary>
/// Decides whether a plugin may open a file on the SbConsole host (design spec
/// docs/superpowers/specs/2026-10-13-sqlite-plugin-design.md §3). Implemented by the host, which
/// alone knows its allowed roots and where its own database lives. Callers must open
/// <see cref="PathCheckResult.ResolvedPath"/>, never the path they passed in: that removes any
/// symlink that existed when the check ran. It does not make check-then-open atomic -- someone
/// with write access under an allowed root can still race a new symlink in after the check.
/// </summary>
public interface IHostPathPolicy
{
    PathCheckResult Check(string path);
}

public sealed record PathCheckResult(bool Allowed, string? ResolvedPath, string? Reason)
{
    public static PathCheckResult Allow(string resolvedPath) => new(true, resolvedPath, null);

    public static PathCheckResult Refuse(string reason) => new(false, null, reason);
}
