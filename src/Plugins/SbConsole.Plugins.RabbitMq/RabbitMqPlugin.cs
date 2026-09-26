using Microsoft.Extensions.DependencyInjection;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.RabbitMq;

public sealed class RabbitMqPlugin : IPlugin
{
    public string Id => "rabbitmq";
    public string DisplayName => "RabbitMQ";
    public string Version => "1.0.0";
    public IReadOnlyList<PluginNavItem> NavItems =>
    [
        new("Overview", "/p/rabbitmq/overview"),
        new("Exchanges", "/p/rabbitmq/exchanges"),
        new("Queues", "/p/rabbitmq/queues"),
        new("Shovels & policies", "/p/rabbitmq/shovels"),
    ];
    public string ConnectionKind => "rabbitmq";
    public string ConnectionKindDisplayName => "RabbitMQ";

    // Placeholder: Task 13 of the RabbitMQ plan sets the real page/action counts once every page
    // and handler exists.
    public PluginContribution Contribution => new(PageCount: 0, ActionCount: 0);

    // RabbitOperations is stateless, so one instance serves every connection. Then one scoped
    // registration per handler class; pages resolve handlers by concrete type (no MediatR). A new
    // handler must be added here or its page fails to render at runtime.
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IRabbitOperations, RabbitOperations>();
        services.AddScoped<Connections.GetConnectionEchoQueryHandler>();
        services.AddScoped<Connections.ListVhostsQueryHandler>();
        services.AddScoped<Overview.GetOverviewQueryHandler>();
        services.AddScoped<Exchanges.ListExchangesQueryHandler>();
        services.AddScoped<Exchanges.ListExchangeBindingsQueryHandler>();
        services.AddScoped<Exchanges.CreateExchangeCommandHandler>();
        services.AddScoped<Exchanges.DeleteExchangeCommandHandler>();
        services.AddScoped<Queues.ListQueuesQueryHandler>();
        services.AddScoped<Queues.GetQueueDetailQueryHandler>();
        services.AddScoped<Queues.CreateQueueCommandHandler>();
        services.AddScoped<Queues.DeleteQueueCommandHandler>();
        services.AddScoped<Queues.PurgeQueueCommandHandler>();
        services.AddScoped<Bindings.AddBindingCommandHandler>();
        services.AddScoped<Bindings.RemoveBindingCommandHandler>();
        services.AddScoped<Messages.PeekMessagesQueryHandler>();
        services.AddScoped<Messages.ConsumeMessagesCommandHandler>();
        services.AddScoped<Messages.PublishMessageCommandHandler>();
        services.AddScoped<Messages.RepublishMessagesCommandHandler>();
        services.AddScoped<Shovels.ListShovelsQueryHandler>();
        services.AddScoped<Shovels.CreateShovelCommandHandler>();
        services.AddScoped<Shovels.DeleteShovelCommandHandler>();
        services.AddScoped<Shovels.RestartShovelCommandHandler>();
        services.AddScoped<Policies.ListPoliciesQueryHandler>();
    }

    // The host calls this on a new()'d plugin, outside DI (AwsPlugin precedent), so it builds its
    // own RabbitOperations. The split AMQP/management result never throws for a bad secret or an
    // unreachable broker -- both come back as a failed ConnectionTestResult.
    public Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default) =>
        new RabbitOperations().TestConnectionAsync(secret, ct);
}
