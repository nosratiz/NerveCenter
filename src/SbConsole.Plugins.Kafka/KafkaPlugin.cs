using Microsoft.Extensions.DependencyInjection;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka;

public sealed class KafkaPlugin : IPlugin
{
    public string Id => "kafka";
    public string DisplayName => "Apache Kafka";
    public string Version => "1.0.0";
    public IReadOnlyList<PluginNavItem> NavItems =>
    [
        new("Topics", "/p/kafka/topics"),
    ];
    public string ConnectionKind => "kafka";
    public string ConnectionKindDisplayName => "Apache Kafka";

    // Topics: Create/Delete topic, Peek, Send (4 -- Peek/Send land in Task 7). Pages: Topics, Peek.
    public PluginContribution Contribution => new(PageCount: 2, ActionCount: 4);

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IKafkaOperations, ConfluentKafkaOperations>();
        services.AddScoped<Topics.ListTopicsQueryHandler>();
        services.AddScoped<Topics.CreateTopicCommandHandler>();
        services.AddScoped<Topics.DeleteTopicCommandHandler>();
        services.AddScoped<Topics.GetConnectionEchoQueryHandler>();
    }

    // Plugins are constructed via a parameterless new() (AddSbConsolePlugin<TPlugin>()'s `new()`
    // constraint), so there's no DI container to pull a registered IKafkaOperations from at this
    // layer -- construct the real implementation directly, same as ServiceBusPlugin does.
    public Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default) =>
        new ConfluentKafkaOperations().TestConnectionAsync(secret, ct);

    public async Task<IReadOnlyList<PluginDashboardMetric>> GetDashboardMetricsAsync(string connectionString, CancellationToken ct = default)
    {
        var topics = await new ConfluentKafkaOperations().ListTopicsAsync(connectionString, ct);
        return
        [
            new PluginDashboardMetric("Topics", topics.Count),
            new PluginDashboardMetric("Partitions", topics.Sum(t => t.PartitionCount)),
        ];
    }
}
