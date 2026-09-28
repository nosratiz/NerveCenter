using StackExchange.Redis;

namespace SbConsole.Plugins.Redis.Client;

/// <summary>
/// Best-effort, non-authoritative check of whether an <c>ACL GETUSER</c> "commands" rule string
/// grants a specific command -- used only to proactively disable a button in the UI (design spec
/// §14: FLUSHDB and PUBLISH). The real authorization boundary always stays server-side: a command
/// that's actually denied still fails loudly and correctly via NOPERM (<see cref="FriendlyRedisError"/>)
/// if this approximation is ever wrong in the allow direction -- this type never gates anything but
/// the button's Disabled state.
///
/// Redis's own rule evaluation is "apply every +/- token left-to-right, last match wins" across
/// +@all/-@all, +@category/-@category, and +cmd/-cmd tokens (see ACL GETUSER's "commands" field in
/// the Redis ACL docs). This evaluator implements exactly that ordering, but only recognizes the
/// category names the two phase-3 call sites actually need (see
/// <see cref="RedisOperations"/>'s category map) -- not the full Redis ACL category table -- per the
/// design brief's own "keep this scoped" instruction; nothing else in the plugin uses this yet.
/// </summary>
public static class RedisAclPermissionEvaluator
{
    public static bool IsAllowed(string? commandsField, string command, IReadOnlyList<string> categories)
    {
        // No ACL data to reason about (GETUSER failed, returned nothing parseable, or the server
        // predates ACL) -- fail open, since this hook only drives a UI hint, not real enforcement.
        if (string.IsNullOrWhiteSpace(commandsField))
        {
            return true;
        }

        var normalizedCommand = command.Trim();
        var normalizedCategories = new HashSet<string>(categories.Select(c => c.Trim()), StringComparer.OrdinalIgnoreCase);

        var allowed = true;
        foreach (var raw in commandsField.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw.Equals("allcommands", StringComparison.OrdinalIgnoreCase))
            {
                allowed = true;
                continue;
            }

            if (raw.Equals("nocommands", StringComparison.OrdinalIgnoreCase))
            {
                allowed = false;
                continue;
            }

            if (raw.Length < 2 || raw[0] is not ('+' or '-'))
            {
                continue; // not a grant/revoke token (e.g. a key/channel selector) -- not our concern here.
            }

            var grant = raw[0] == '+';
            var name = raw[1..];
            var pipeIndex = name.IndexOf('|'); // drop a "|subcommand" suffix, e.g. "+acl|whoami".
            if (pipeIndex >= 0)
            {
                name = name[..pipeIndex];
            }

            if (name.Equals("@all", StringComparison.OrdinalIgnoreCase) ||
                name.Equals(normalizedCommand, StringComparison.OrdinalIgnoreCase))
            {
                allowed = grant;
            }
            else if (name.StartsWith('@') && normalizedCategories.Contains(name[1..]))
            {
                allowed = grant;
            }
        }

        return allowed;
    }

    /// <summary>
    /// Extracts the "commands" field from an <c>ACL GETUSER</c> reply -- a flat field/value array in
    /// both the Redis 6.x and 7.x reply shapes (7.x adds a "selectors" field whose own value is a
    /// nested array, which this loop skips over correctly since it only ever reads pairs).
    /// </summary>
    public static string? ExtractCommandsField(RedisResult reply)
    {
        if (reply.IsNull)
        {
            return null;
        }

        var fields = (RedisResult[]?)reply;
        if (fields is null)
        {
            return null;
        }

        for (var i = 0; i + 1 < fields.Length; i += 2)
        {
            var key = (string?)fields[i];
            if (string.Equals(key, "commands", StringComparison.OrdinalIgnoreCase))
            {
                return (string?)fields[i + 1];
            }
        }

        return null;
    }
}
