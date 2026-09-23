namespace SbConsole.Plugins.ServiceBus.Client;

public sealed record SendMessageRequest(
    string Body,
    string ContentType = "application/json",
    IReadOnlyDictionary<string, string>? Properties = null,
    DateTimeOffset? ScheduledEnqueueTime = null);
