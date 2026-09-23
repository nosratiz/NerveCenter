using Microsoft.Extensions.DependencyInjection;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Queues;
using SbConsole.Plugins.Aws.Subscriptions;
using SbConsole.Plugins.Aws.Topics;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws;

public sealed class AwsPlugin : IPlugin
{
    public string Id => "aws";
    public string DisplayName => "AWS SQS/SNS";
    public string Version => "1.0.0";
    public IReadOnlyList<PluginNavItem> NavItems =>
    [
        new("Queues", "/p/aws/queues"),
        new("Topics", "/p/aws/topics"),
    ];
    public string ConnectionKind => "aws";
    public string ConnectionKindDisplayName => "AWS SQS/SNS";

    // Queues: Create/Delete/Purge queue, Receive, Delete message, Release message, Send, Redrive (8).
    // Topics: Create/Delete topic, Subscribe/Unsubscribe, Publish (5).
    // Pages: Queues, Receive, Topics, TopicDetail (4).
    public PluginContribution Contribution => new(PageCount: 4, ActionCount: 13);

    // The Queues/Messages/Redrive command and query handlers are added to this project one at a
    // time by Tasks 4, 7, 8, 9, 10, 12, 13 -- registering them here in Task 3 (as the plan's
    // AwsPlugin.cs listing does) would reference types that don't exist yet and fail the build.
    // Each of those later tasks adds its own `services.AddScoped<...>()` line here as it creates
    // the corresponding handler class.
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<ISqsOperations, SqsOperations>();
        services.AddSingleton<ISnsOperations, SnsOperations>();
        services.AddScoped<ListQueuesQueryHandler>();
        services.AddScoped<ListTopicsQueryHandler>();
        services.AddScoped<CreateTopicCommandHandler>();
        services.AddScoped<DeleteTopicCommandHandler>();
        services.AddScoped<ListSubscriptionsQueryHandler>();
        services.AddScoped<SubscribeCommandHandler>();
        services.AddScoped<UnsubscribeCommandHandler>();
        services.AddScoped<PublishCommandHandler>();
        services.AddScoped<GetSubscriptionFilterPoliciesQueryHandler>();
        services.AddScoped<GetTopicDeliveryFailureCountQueryHandler>();
        services.AddScoped<GetConnectionEchoQueryHandler>();
        services.AddScoped<Queues.DeleteQueueCommandHandler>();
        services.AddScoped<Queues.CreateQueueCommandHandler>();
        services.AddScoped<Queues.PurgeQueueCommandHandler>();
        services.AddScoped<Messages.ReceiveMessagesCommandHandler>();
        services.AddScoped<Messages.DeleteMessageCommandHandler>();
        services.AddScoped<Messages.ReleaseMessageCommandHandler>();
        services.AddScoped<Messages.SendMessageCommandHandler>();
        services.AddScoped<Redrive.StartRedriveCommandHandler>();
    }

    // Plugins are constructed via a parameterless new() (AddSbConsolePlugin<TPlugin>()'s `new()`
    // constraint), so there's no DI container to pull a registered ISqsOperations from at this
    // layer -- construct the real implementation directly, same as KafkaPlugin/ServiceBusPlugin.
    public Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default) =>
        new SqsOperations().TestConnectionAsync(secret, ct);

    // A default-interface-member implementation of IPlugin.GetNavBadgeAsync is not callable
    // through an AwsPlugin-typed reference (C# only dispatches unoverridden DIMs through an
    // interface-typed reference) -- AwsPluginTests calls it via a concrete `AwsPlugin` variable,
    // same as KafkaPluginTests/ServiceBusPluginTests do for their own plugins, both of which
    // explicitly implement this method for the same reason. Trivial override: nothing to report
    // until this plugin has a dashboard/DLQ-overview plan of its own (design spec §9, Out of scope).
    public Task<int?> GetNavBadgeAsync(string navItemHref, string connectionString, CancellationToken ct = default) =>
        Task.FromResult<int?>(null);

    // GetDashboardMetricsAsync, GetDashboardProblemsAsync, GetOldestDeadLetterAsync,
    // GetResourceMetricsAsync: SDK defaults (design spec §9, Out of scope) -- nothing to report
    // until this plugin has a dashboard/DLQ-overview plan of its own.
}
