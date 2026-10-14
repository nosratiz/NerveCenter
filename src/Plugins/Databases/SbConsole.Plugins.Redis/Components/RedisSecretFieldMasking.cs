namespace SbConsole.Plugins.Redis.Components;

/// <summary>
/// Which hash fields HashEditor masks by default. Case-insensitive substring match against a
/// constant list, per the design spec §7a -- a real per-plugin setting is out of scope for now.
/// </summary>
public static class RedisSecretFieldMasking
{
    private static readonly string[] SensitivePatterns = ["csrf", "token", "secret"];

    public static bool IsSensitive(string fieldName) =>
        SensitivePatterns.Any(pattern => fieldName.Contains(pattern, StringComparison.OrdinalIgnoreCase));
}
