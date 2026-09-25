using Microsoft.Extensions.Logging;
using SbConsole.Sdk;

namespace SbConsole.Plugins.RabbitMq.Client;

/// <summary>
/// The real <see cref="IRabbitOperations"/> (design spec §4): composes the management-API client,
/// the AMQP client and the split Test-connection logic. Every method resolves
/// <see cref="RabbitConnectionSettings"/> from the raw secret itself -- nothing is pre-configured
/// for one connection -- so a malformed secret surfaces as the settings'
/// InvalidOperationException before any network traffic. Stateless and thread-safe; registered as
/// a singleton.
/// </summary>
public sealed class RabbitOperations : IRabbitOperations
{
    private readonly ManagementApiClient _management;
    private readonly AmqpClient _amqp;
    private readonly ConnectionTester _tester;

    public RabbitOperations()
        : this(new ManagementApiClient(), new AmqpClient(), TimeProvider.System, logger: null)
    {
    }

    public RabbitOperations(ILogger<RabbitOperations> logger)
        : this(new ManagementApiClient(), new AmqpClient(), TimeProvider.System, logger)
    {
    }

    internal RabbitOperations(ManagementApiClient management, AmqpClient amqp, TimeProvider clock, ILogger? logger)
    {
        _management = management;
        _amqp = amqp;
        _tester = new ConnectionTester(amqp.ProbeAsync, management.ProbeAsync, clock, logger);
    }

    public Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default) =>
        _tester.TestAsync(secret, ct);

    // ---------------------------------------------------------------- management API: reads

    public async Task<IReadOnlyList<string>> ListVhostsAsync(string secret, CancellationToken ct = default) =>
        await _management.ListVhostsAsync(Settings(secret), ct);

    public async Task<BrokerOverview> GetOverviewAsync(string secret, CancellationToken ct = default) =>
        await _management.GetOverviewAsync(Settings(secret), ct);

    public async Task<IReadOnlyList<NodeSummary>> ListNodesAsync(string secret, CancellationToken ct = default) =>
        await _management.ListNodesAsync(Settings(secret), ct);

    public async Task<IReadOnlyList<ExchangeSummary>> ListExchangesAsync(string secret, string vhost, CancellationToken ct = default) =>
        await _management.ListExchangesAsync(Settings(secret), vhost, ct);

    public async Task<IReadOnlyList<QueueSummary>> ListQueuesAsync(string secret, string? vhost, CancellationToken ct = default) =>
        await _management.ListQueuesAsync(Settings(secret), vhost, ct);

    public async Task<QueueDetails> GetQueueAsync(string secret, string vhost, string queue, CancellationToken ct = default) =>
        await _management.GetQueueAsync(Settings(secret), vhost, queue, ct);

    public async Task<IReadOnlyList<BindingInfo>> ListBindingsAsync(string secret, string? vhost, CancellationToken ct = default) =>
        await _management.ListBindingsAsync(Settings(secret), vhost, ct);

    public async Task<IReadOnlyList<ShovelInfo>> ListShovelsAsync(string secret, string vhost, CancellationToken ct = default) =>
        await _management.ListShovelsAsync(Settings(secret), vhost, ct);

    public async Task<IReadOnlyList<PolicyInfo>> ListPoliciesAsync(string secret, string vhost, CancellationToken ct = default) =>
        await _management.ListPoliciesAsync(Settings(secret), vhost, ct);

    // ---------------------------------------------------------------- management API: writes

    public async Task CreateExchangeAsync(string secret, string vhost, CreateExchangeRequest request, CancellationToken ct = default) =>
        await _management.CreateExchangeAsync(Settings(secret), vhost, request, ct);

    public async Task DeleteExchangeAsync(string secret, string vhost, string exchange, CancellationToken ct = default) =>
        await _management.DeleteExchangeAsync(Settings(secret), vhost, exchange, ct);

    public async Task CreateQueueAsync(string secret, string vhost, CreateQueueRequest request, CancellationToken ct = default) =>
        await _management.CreateQueueAsync(Settings(secret), vhost, request, ct);

    public async Task DeleteQueueAsync(string secret, string vhost, string queue, CancellationToken ct = default) =>
        await _management.DeleteQueueAsync(Settings(secret), vhost, queue, ct);

    public async Task PurgeQueueAsync(string secret, string vhost, string queue, CancellationToken ct = default) =>
        await _management.PurgeQueueAsync(Settings(secret), vhost, queue, ct);

    public async Task AddBindingAsync(
        string secret, string vhost, string exchange, string queue, string routingKey,
        IReadOnlyDictionary<string, object?> arguments, CancellationToken ct = default) =>
        await _management.AddBindingAsync(Settings(secret), vhost, exchange, queue, routingKey, arguments, ct);

    public async Task RemoveBindingAsync(
        string secret, string vhost, string exchange, string queue, string propertiesKey, CancellationToken ct = default) =>
        await _management.RemoveBindingAsync(Settings(secret), vhost, exchange, queue, propertiesKey, ct);

    public async Task CreateShovelAsync(string secret, string vhost, CreateShovelRequest request, CancellationToken ct = default) =>
        await _management.CreateShovelAsync(Settings(secret), vhost, request, ct);

    public async Task DeleteShovelAsync(string secret, string vhost, string name, CancellationToken ct = default) =>
        await _management.DeleteShovelAsync(Settings(secret), vhost, name, ct);

    public async Task RestartShovelAsync(string secret, string vhost, string name, CancellationToken ct = default) =>
        await _management.RestartShovelAsync(Settings(secret), vhost, name, ct);

    // ---------------------------------------------------------------- AMQP

    public async Task<IReadOnlyList<RabbitMessage>> GetMessagesAsync(
        string secret, string vhost, string queue, int count, GetMode mode, CancellationToken ct = default) =>
        await _amqp.GetMessagesAsync(Settings(secret), vhost, queue, count, mode, ct);

    public async Task<PublishOutcome> PublishAsync(string secret, string vhost, PublishRequest request, CancellationToken ct = default) =>
        await _amqp.PublishAsync(Settings(secret), vhost, request, ct);

    private static RabbitConnectionSettings Settings(string secret) => RabbitConnectionSettings.From(secret);
}
