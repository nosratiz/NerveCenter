namespace SbConsole.Plugins.RabbitMq.Client;

/// <summary>
/// One binding. DestinationType is "queue" or "exchange". PropertiesKey is the management API's
/// handle for deleting exactly this binding (routing key + arguments hash).
/// </summary>
public sealed record BindingInfo(
    string Source,
    string Destination,
    string DestinationType,
    string RoutingKey,
    IReadOnlyDictionary<string, object?> Arguments,
    string PropertiesKey)
{
    public bool IsToQueue => DestinationType == "queue";
}
