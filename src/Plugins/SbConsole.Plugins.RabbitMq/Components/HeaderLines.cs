namespace SbConsole.Plugins.RabbitMq.Components;

/// <summary>
/// Parses the publish dialog's headers textarea: one <c>key: value</c> per line, split on the
/// first colon, both sides trimmed, blank lines ignored. Pure. Every bad line is reported (with
/// its 1-based line number) rather than silently dropped -- a header the user typed and we
/// didn't send would change routing on a headers exchange without anyone noticing.
/// </summary>
public static class HeaderLines
{
    public static (IReadOnlyDictionary<string, string> Headers, IReadOnlyList<string> Errors) Parse(string? text)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return (headers, errors);
        }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var number = i + 1;
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                errors.Add($"Line {number}: expected \"key: value\".");
                continue;
            }

            var key = line[..colon].Trim();
            if (key.Length == 0)
            {
                errors.Add($"Line {number}: header name is empty.");
                continue;
            }

            if (!headers.TryAdd(key, line[(colon + 1)..].Trim()))
            {
                errors.Add($"Line {number}: header \"{key}\" is repeated.");
            }
        }

        return (headers, errors);
    }
}
