namespace SbConsole.Plugins.Aws.Client;

/// <summary>
/// One SNS delivery-status log event from CloudWatch Logs. <see cref="Timestamp"/> is the log
/// event's own CloudWatch timestamp (always present, so rows sort consistently). Every other field
/// is optional because the event shape varies by protocol -- SMS logs, for example, carry no
/// statusCode or attempts. When the event text isn't a JSON object, only <see cref="RawMessage"/>
/// is set and <see cref="Status"/> comes from which log group it was read from.
/// </summary>
public sealed record DeliveryLogEntry(
    DateTimeOffset Timestamp,
    string Status,
    string? MessageId,
    string? Destination,
    int? StatusCode,
    string? ProviderResponse,
    long? DwellTimeMs,
    int? Attempts,
    string? RawMessage = null)
{
    public bool IsFailure => string.Equals(Status, "FAILURE", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Recent delivery-status log events for a topic, newest first.
/// <see cref="LoggingNotConfigured"/>: neither the success nor the /Failure log group exists.
/// <see cref="IsTruncated"/>: the window may hold more events than returned (limit reached, or the
/// bounded scan stopped early).
/// </summary>
public sealed record DeliveryLogsResult(IReadOnlyList<DeliveryLogEntry> Entries, bool LoggingNotConfigured, bool IsTruncated);
