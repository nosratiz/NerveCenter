namespace SbConsole.Sdk;

/// <summary>
/// One problem a plugin wants surfaced on the host Dashboard's "Needs attention" section, for one
/// connection. The host calls IPlugin.GetDashboardProblemsAsync once per connection and merges the
/// results with its own host-level problems (e.g. an unreachable connection) into one list, sorted
/// Errors before Warnings.
/// </summary>
public sealed record PluginDashboardProblem(
    string Severity,   // "Error" | "Warning"
    string Title,      // e.g. "payments-dlq", or "notify-fanout / sms"
    string Detail,     // e.g. "214 dead-lettered, +38 in the last hour" -- the host prefixes the
                        // connection name when rendering, so Detail itself never repeats it
    string? LinkHref); // e.g. "/p/azure-servicebus/dead-letter" -- rendered as a link when present
