namespace SbConsole.Plugins.RabbitMq.Client;

/// <summary>
/// A non-2xx response from the RabbitMQ management HTTP API. <see cref="Path"/> is the escaped
/// request path (e.g. "/api/exchanges/%2Forders") and <see cref="Reason"/> the broker's "reason"
/// field when the body carried one. FriendlyRabbitError maps it to a user-facing message.
/// </summary>
public sealed class ManagementApiException(int statusCode, string method, string path, string? reason)
    : Exception($"{method} {path} → {statusCode}" + (string.IsNullOrWhiteSpace(reason) ? "" : $": {reason}"))
{
    public int StatusCode { get; } = statusCode;

    public string Method { get; } = method;

    public string Path { get; } = path;

    public string? Reason { get; } = reason;
}
