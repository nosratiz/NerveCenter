using Microsoft.Extensions.DependencyInjection;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka;

public sealed class KafkaPlugin : IPlugin
{
    // Shared by ConsumerGroups.razor's warning chip (Task 6) and GetDashboardProblemsAsync/
    // GetNavBadgeAsync (Task 8) so the threshold is defined exactly once. See design spec §6.
    internal const long LagProblemThreshold = 10_000;

    public string Id => "kafka";
    public string DisplayName => "Apache Kafka";
    public string Version => "1.0.0";
    public IReadOnlyList<PluginNavItem> NavItems =>
    [
        new("Topics", "/p/kafka/topics"),
        new("Consumer Groups", "/p/kafka/consumer-groups"),
    ];
    public string ConnectionKind => "kafka";
    public string ConnectionKindDisplayName => "Apache Kafka";

    // Topics: Create/Delete topic, Peek, Produce, Reset offset (5).
    // Pages: Topics, Peek, ConsumerGroups, ConsumerGroupDetail (4).
    public PluginContribution Contribution => new(PageCount: 4, ActionCount: 5);

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IKafkaOperations, ConfluentKafkaOperations>();
        services.AddScoped<Topics.ListTopicsQueryHandler>();
        services.AddScoped<Topics.CreateTopicCommandHandler>();
        services.AddScoped<Topics.DeleteTopicCommandHandler>();
        services.AddScoped<Topics.GetConnectionEchoQueryHandler>();
        services.AddScoped<Messages.PeekMessagesQueryHandler>();
        services.AddScoped<Messages.ProduceMessageCommandHandler>();
        services.AddScoped<ConsumerGroups.ListConsumerGroupsQueryHandler>();
        services.AddScoped<ConsumerGroups.GetConsumerGroupDetailQueryHandler>();
        services.AddScoped<ConsumerGroups.ResetConsumerGroupOffsetCommandHandler>();
    }

    // Plugins are constructed via a parameterless new() (AddSbConsolePlugin<TPlugin>()'s `new()`
    // constraint), so there's no DI container to pull a registered IKafkaOperations from at this
    // layer -- construct the real implementation directly, same as ServiceBusPlugin does.
    public Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default) =>
        new ConfluentKafkaOperations().TestConnectionAsync(secret, ct);

    public async Task<IReadOnlyList<PluginDashboardMetric>> GetDashboardMetricsAsync(string connectionString, CancellationToken ct = default)
    {
        // GetTopicCountsAsync, not ListTopicsAsync -- this runs on every Dashboard render and every
        // Wallboard refresh, and neither metric below needs the per-partition watermark walk
        // ListTopicsAsync pays for.
        var (topicCount, partitionCount) = await new ConfluentKafkaOperations().GetTopicCountsAsync(connectionString, ct);
        return
        [
            new PluginDashboardMetric("Topics", topicCount),
            new PluginDashboardMetric("Partitions", partitionCount),
        ];
    }

    // GetDashboardProblemsAsync/GetNavBadgeAsync overrides are added in Task 8.
}
