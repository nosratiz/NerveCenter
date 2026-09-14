using System.Diagnostics.CodeAnalysis;

namespace SbConsole.Sdk;

/// <summary>
/// The single UI/persistence boundary for failure text. Raw Azure SDK exception messages are
/// routinely multi-line and thousands of characters long (a retry-exhausted
/// <c>AggregateException</c> repeats every inner message, and some inner messages embed AMQP
/// stack text), which made snackbars cover half the viewport and the Connections page's Status
/// column wrap five lines. Everything that reaches a user or a database column goes through here.
/// <para>
/// This lives in <c>SbConsole.Sdk</c> because it is the only assembly both <c>SbConsole.Core</c>
/// (which persists <c>Connection.LastTestError</c> and audit details) and
/// <c>SbConsole.Plugins.ServiceBus</c> (which builds <c>PluginResult.Fail</c> messages) reference.
/// </para>
/// <para>
/// Truncating here is deliberately NOT a diagnostics loss: every call site logs the full exception
/// via <c>ILogger</c> before reducing it, which is what the trailing marker points at.
/// </para>
/// </summary>
public static class FriendlyError
{
    /// <summary>Maximum length of the message text itself, before the marker is appended.</summary>
    public const int MaxLength = 200;

    /// <summary>Appended when text was cut, so the reader knows where the rest went.</summary>
    public const string TruncationMarker = " (see server logs for details)";

    /// <summary>
    /// Reduces an exception to a short, single-line message safe to show and to persist. Uses only
    /// <see cref="Exception.Message"/> — never <see cref="Exception.ToString()"/> — so a stack trace
    /// can never enter this path in the first place.
    /// </summary>
    public static string From(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var message = Truncate(ex.Message);
        return string.IsNullOrWhiteSpace(message)
            ? $"{ex.GetType().Name}{TruncationMarker}"
            : message;
    }

    /// <summary>
    /// Collapses whitespace runs (including the newlines that make a table cell wrap) to single
    /// spaces and cuts the result to <see cref="MaxLength"/>, appending
    /// <see cref="TruncationMarker"/> when anything was cut. Null in, null out — a successful
    /// result's absent error message stays absent.
    /// </summary>
    [return: NotNullIfNotNull(nameof(message))]
    public static string? Truncate(string? message)
    {
        if (message is null)
        {
            return null;
        }

        var collapsed = CollapseWhitespace(message);
        return collapsed.Length <= MaxLength
            ? collapsed
            : string.Concat(collapsed.AsSpan(0, MaxLength).TrimEnd(), TruncationMarker);
    }

    private static string CollapseWhitespace(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        var lastWasWhitespace = false;
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                lastWasWhitespace = true;
                continue;
            }

            if (lastWasWhitespace && builder.Length > 0)
            {
                builder.Append(' ');
            }

            lastWasWhitespace = false;
            builder.Append(c);
        }

        return builder.ToString();
    }
}
