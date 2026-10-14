namespace SbConsole.Plugins.ServiceBus.Client;

public sealed record CorrelationMatch(
    string? CorrelationId = null,
    string? Label = null,
    string? MessageId = null,
    string? To = null,
    string? ReplyTo = null,
    string? SessionId = null,
    string? ReplyToSessionId = null,
    string? ContentType = null);
