using SbConsole.Sdk;

namespace SbConsole.Plugins.RabbitMq.Client;

/// <summary>
/// The seam between the plugin's handlers/pages and RabbitMQ. Every method takes the raw
/// connection secret -- no instance is pre-configured for one connection -- same as
/// ISqsOperations/IKafkaOperations. Management-API methods throw ManagementApiException on a
/// non-2xx response; AMQP methods throw RabbitMQ.Client exceptions. Callers route both through
/// FriendlyRabbitError. See design spec §4.
/// </summary>
public interface IRabbitOperations
{
    /// <summary>Probes AMQP and the management API separately and reports both (design spec §2).</summary>
    Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default);

    // --- management API: reads ---
    Task<IReadOnlyList<string>> ListVhostsAsync(string secret, CancellationToken ct = default);
    Task<BrokerOverview> GetOverviewAsync(string secret, CancellationToken ct = default);
    Task<IReadOnlyList<NodeSummary>> ListNodesAsync(string secret, CancellationToken ct = default);
    Task<IReadOnlyList<ExchangeSummary>> ListExchangesAsync(string secret, string vhost, CancellationToken ct = default);

    /// <summary>vhost null = every vhost the user can see (GET /api/queues).</summary>
    Task<IReadOnlyList<QueueSummary>> ListQueuesAsync(string secret, string? vhost, CancellationToken ct = default);
    Task<QueueDetails> GetQueueAsync(string secret, string vhost, string queue, CancellationToken ct = default);

    /// <summary>vhost null = every vhost the user can see (GET /api/bindings).</summary>
    Task<IReadOnlyList<BindingInfo>> ListBindingsAsync(string secret, string? vhost, CancellationToken ct = default);
    Task<IReadOnlyList<ShovelInfo>> ListShovelsAsync(string secret, string vhost, CancellationToken ct = default);
    Task<IReadOnlyList<PolicyInfo>> ListPoliciesAsync(string secret, string vhost, CancellationToken ct = default);

    // --- management API: writes ---
    Task CreateExchangeAsync(string secret, string vhost, CreateExchangeRequest request, CancellationToken ct = default);
    Task DeleteExchangeAsync(string secret, string vhost, string exchange, CancellationToken ct = default);
    Task CreateQueueAsync(string secret, string vhost, CreateQueueRequest request, CancellationToken ct = default);
    Task DeleteQueueAsync(string secret, string vhost, string queue, CancellationToken ct = default);
    Task PurgeQueueAsync(string secret, string vhost, string queue, CancellationToken ct = default);
    Task AddBindingAsync(string secret, string vhost, string exchange, string queue, string routingKey, IReadOnlyDictionary<string, object?> arguments, CancellationToken ct = default);
    Task RemoveBindingAsync(string secret, string vhost, string exchange, string queue, string propertiesKey, CancellationToken ct = default);
    Task CreateShovelAsync(string secret, string vhost, CreateShovelRequest request, CancellationToken ct = default);
    Task DeleteShovelAsync(string secret, string vhost, string name, CancellationToken ct = default);
    Task RestartShovelAsync(string secret, string vhost, string name, CancellationToken ct = default);

    // --- AMQP ---

    /// <summary>
    /// basic.get up to <paramref name="count"/> messages on one channel, then nack-requeue all
    /// (Peek) or ack all (Consume). Cancellation or failure before that closes the channel, which
    /// requeues everything held.
    /// </summary>
    Task<IReadOnlyList<RabbitMessage>> GetMessagesAsync(string secret, string vhost, string queue, int count, GetMode mode, CancellationToken ct = default);

    /// <summary>Publishes with mandatory=true and publisher confirms; a basic.return is Unroutable.</summary>
    Task<PublishOutcome> PublishAsync(string secret, string vhost, PublishRequest request, CancellationToken ct = default);
}
