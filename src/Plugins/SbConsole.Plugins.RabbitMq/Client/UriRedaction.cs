using System.Text.RegularExpressions;

namespace SbConsole.Plugins.RabbitMq.Client;

/// <summary>
/// Strips userinfo from amqp(s)/http(s) URIs quoted in broker text (a shovel validation error, say,
/// echoes the src-uri with its password) before it reaches an exception message, the UI, an audit
/// row or a log. The userinfo runs to the LAST '@' of the authority, so a password containing '@'
/// ("amqp://user:p@ss@remote") is removed whole.
/// </summary>
internal static partial class UriRedaction
{
    [GeneratedRegex("""(amqps?|https?)://[^/\s"']+@""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UserInfo();

    public static string Redact(string text) => UserInfo().Replace(text, "$1://***@");

    public static string? RedactOrNull(string? text) => text is null ? null : Redact(text);
}
