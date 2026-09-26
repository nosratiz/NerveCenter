using NSubstitute;
using SbConsole.Plugins.RabbitMq.Client;

namespace SbConsole.Plugins.RabbitMq.Tests.Pages;

/// <summary>
/// A canned /orders vhost shaped like docker/rabbitmq's seed: a topic exchange with wildcard
/// bindings, a DLX, an exchange nobody bound (still receiving traffic), an exchange with an
/// alternate exchange, the default exchange and the amq.* built-ins.
/// </summary>
internal static class SeededTopology
{
    public const string Vhost = "/orders";

    private static readonly IReadOnlyDictionary<string, object?> NoArgs = new Dictionary<string, object?>();

    public static ExchangeSummary Exchange(string name, string type, double? inRate = null, double? outRate = null,
        string? alternateExchange = null, bool durable = true, bool autoDelete = false, bool @internal = false) =>
        new(name, type, durable, autoDelete, @internal, alternateExchange, inRate, outRate, NoArgs);

    public static BindingInfo Binding(string source, string destination, string routingKey,
        string destinationType = "queue", IReadOnlyDictionary<string, object?>? args = null) =>
        new(source, destination, destinationType, routingKey, args ?? NoArgs, routingKey.Length == 0 ? "~" : routingKey);

    public static QueueSummary Queue(string name, string? dlx = null) =>
        new(Vhost, name, "classic", true, false, false, 0, 0, 1, null, null, null, dlx, null, null, null, null, false, null, null, 0, null, null, NoArgs, NoArgs);

    public static List<ExchangeSummary> Exchanges() =>
    [
        Exchange("", "direct", 4, 4),
        Exchange("amq.direct", "direct"),
        Exchange("amq.topic", "topic"),
        Exchange("order-events", "topic", 1204, 1204),
        Exchange("billing.retry.dlx", "direct", 37, 37),
        Exchange("legacy.import", "direct", 37, 0),
        Exchange("old.unused", "direct", 0, 0),
        Exchange("notify.fanout", "fanout", 402, 1608),
        Exchange("invoice.headers", "headers", 12, 12),
        Exchange("shipment.updates", "topic", 96, 96, alternateExchange: "unrouted.ae"),
        Exchange("unrouted.ae", "fanout", 3, 3, @internal: true),
    ];

    public static List<BindingInfo> Bindings() =>
    [
        Binding("order-events", "order-events.q", "order.*.created"),
        Binding("order-events", "audit.sink", "#"),
        Binding("order-events", "order-events.q", "order.*.amended"),
        Binding("billing.retry.dlx", "billing.retry", "billing"),
        Binding("notify.fanout", "notify.email", ""),
        Binding("notify.fanout", "notify.sms", ""),
        Binding("invoice.headers", "invoices.eu", "", args: new Dictionary<string, object?> { ["x-match"] = "all", ["region"] = "eu" }),
        Binding("unrouted.ae", "unrouted.q", ""),
        // The management API lists the default exchange's implicit bindings too; the page counts
        // queues for it instead, so these must not change its figure.
        Binding("", "order-events.q", "order-events.q"),
        Binding("", "audit.sink", "audit.sink"),
    ];

    public static List<QueueSummary> Queues() =>
    [
        Queue("order-events.q"),
        Queue("audit.sink"),
        Queue("billing", dlx: "billing.retry.dlx"),
        Queue("billing.retry"),
        Queue("notify.email"),
        Queue("notify.sms"),
        Queue("invoices.eu"),
        Queue("unrouted.q"),
    ];

    public static void Seed(IRabbitOperations operations, string secret)
    {
        operations.ListExchangesAsync(secret, Vhost, Arg.Any<CancellationToken>()).Returns(Exchanges());
        operations.ListBindingsAsync(secret, Vhost, Arg.Any<CancellationToken>()).Returns(Bindings());
        operations.ListQueuesAsync(secret, Vhost, Arg.Any<CancellationToken>()).Returns(Queues());
    }
}
