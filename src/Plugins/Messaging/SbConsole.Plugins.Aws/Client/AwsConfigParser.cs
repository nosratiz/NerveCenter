namespace SbConsole.Plugins.Aws.Client;

/// <summary>
/// Parses a connection secret shaped as a flat "key=value" string separated by ";" (e.g.
/// "mode=access-keys;region=eu-west-1;accessKeyId=...;secretAccessKey=...") -- see the design
/// spec (docs/superpowers/specs/2026-09-21-aws-sqs-plugin-design.md) §2. Mirrors
/// SbConsole.Plugins.Kafka.Client.KafkaConfigParser exactly: a malformed segment (no "=") is
/// skipped rather than throwing -- the resulting config simply won't have that key, and every
/// operation already surfaces a missing required field as a friendly connection-shaped error
/// (see FriendlyAwsError, Task 2) rather than a parser exception.
/// </summary>
public static class AwsConfigParser
{
    // Fixed echo order deliberately excludes every credential-shaped key (accessKeyId,
    // secretAccessKey, sessionToken, roleArn, externalId, sessionName) so the decrypted secret's
    // credentials never reach the browser, even indirectly -- same allowlist discipline as
    // KafkaConfigParser.SafeEcho.
    private static readonly string[] SafeEchoKeys = ["region", "mode", "endpoint"];

    public static string SafeEcho(string config)
    {
        var parsed = Parse(config);
        return string.Join(" · ", SafeEchoKeys.Where(parsed.ContainsKey).Select(k => $"{k}={parsed[k]}"));
    }

    public static Dictionary<string, string> Parse(string config)
    {
        var result = new Dictionary<string, string>();
        foreach (var segment in config.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = segment.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = segment[..separatorIndex].Trim();
            var value = segment[(separatorIndex + 1)..].Trim();
            if (key.Length == 0)
            {
                continue;
            }

            result[key] = value;
        }

        return result;
    }

    // Fixed key order purely for stable, human-diffable output when a saved secret is inspected
    // directly (e.g. in a DB browser) -- Parse itself is order-independent. AwsConnectionFields
    // (the only caller) never sets a key outside this list; an unrecognized key is silently
    // dropped rather than appended in arbitrary order, matching Parse's own "skip, don't throw"
    // tolerance for anything it doesn't recognize.
    private static readonly string[] SerializeKeyOrder =
    [
        "mode", "region", "accessKeyId", "secretAccessKey", "sessionToken",
        "roleArn", "externalId", "sessionName", "endpoint", "pathStyle",
    ];

    public static string Serialize(IReadOnlyDictionary<string, string> fields)
    {
        var parts = new List<string>();
        foreach (var key in SerializeKeyOrder)
        {
            if (fields.TryGetValue(key, out var value) && value.Length > 0)
            {
                parts.Add($"{key}={value}");
            }
        }

        return string.Join(';', parts);
    }
}
