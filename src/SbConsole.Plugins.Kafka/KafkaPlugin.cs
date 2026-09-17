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

    // Extracted as a pure static function so the threshold boundary is unit-testable without a
    // real broker -- same reasoning as ConfluentKafkaOperations.ComputeLag.
    internal static bool HasHighLag(ConsumerGroupSummary group) => group.TotalLag > LagProblemThreshold;

    public async Task<IReadOnlyList<PluginDashboardProblem>> GetDashboardProblemsAsync(
        Guid connectionId, string connectionString, IPluginStore store, CancellationToken ct = default)
    {
        var groups = await new ConfluentKafkaOperations().ListConsumerGroupsAsync(connectionString, ct);
        return groups
            .Where(HasHighLag)
            .Select(g => new PluginDashboardProblem(
                "Warning",
                $"Consumer group '{g.GroupId}' has high lag",
                $"{g.TotalLag:N0} messages behind",
                $"/p/kafka/consumer-groups/{g.GroupId}"))
            .ToList();
    }

    private const string ConsumerGroupsNavHref = "/p/kafka/consumer-groups";

    public Task<int?> GetNavBadgeAsync(string navItemHref, string connectionString, CancellationToken ct = default) =>
        navItemHref == ConsumerGroupsNavHref
            ? GetConsumerGroupBadgeAsync(connectionString, ct)
            : Task.FromResult<int?>(null);

    private static async Task<int?> GetConsumerGroupBadgeAsync(string connectionString, CancellationToken ct)
    {
        var groups = await new ConfluentKafkaOperations().ListConsumerGroupsAsync(connectionString, ct);
        var problemCount = groups.Count(HasHighLag);
        // null (never 0) when nothing is flagged -- NavMenu.razor renders a visible "0" badge for a
        // literal 0 and only omits the badge for null, confirmed against ServiceBusPlugin's
        // identical GetDeadLetterBadgeAsync pattern (design spec §6).
        return problemCount > 0 ? problemCount : null;
    }
}
