using SbConsole.Plugins.RabbitMq.Client;

namespace SbConsole.Plugins.RabbitMq.Tests.Routing;

internal static class RoutingFixtures
{
    public static readonly IReadOnlyDictionary<string, object?> NoArgs = new Dictionary<string, object?>();
    public static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();

    public static ExchangeSummary Exchange(string name, string type, string? alternateExchange = null) =>
        new(name, type, Durable: true, AutoDelete: false, Internal: false, alternateExchange, null, null, NoArgs);

    public static BindingInfo Binding(string source, string destination, string routingKey,
        IReadOnlyDictionary<string, object?>? args = null, string destinationType = "queue") =>
        new(source, destination, destinationType, routingKey, args ?? NoArgs, routingKey);

    public static QueueSummary Queue(string name, string? dlx = null, string? dlrk = null,
        IReadOnlyDictionary<string, object?>? args = null, string? policy = null, string type = "classic", string vhost = "/") =>
        new(vhost, name, type, Durable: true, AutoDelete: false, Exclusive: false, Ready: 0, Unacked: 0, Consumers: 0,
            PublishRate: null, AckRate: null, RedeliverRate: null, dlx, dlrk, MessageTtlMs: null, MaxLength: null,
            Overflow: null, Lazy: false, policy, IdleSince: null, MemoryBytes: 0, ReplicaCount: null,
            HeadMessageTimestamp: null, args ?? NoArgs, NoArgs);

    public static PolicyInfo Policy(string name, string pattern, string applyTo, int priority = 0) =>
        new(name, pattern, applyTo, priority, NoArgs);
}
