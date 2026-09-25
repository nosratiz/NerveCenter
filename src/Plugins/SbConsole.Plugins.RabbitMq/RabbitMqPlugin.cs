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

    // Task 5 adds the query/command handlers (one scoped registration per handler class, same as
    // AwsPlugin). RabbitOperations is stateless, so one instance serves every connection.
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IRabbitOperations, RabbitOperations>();
    }

    // The host calls this on a new()'d plugin, outside DI (AwsPlugin precedent), so it builds its
    // own RabbitOperations. The split AMQP/management result never throws for a bad secret or an
    // unreachable broker -- both come back as a failed ConnectionTestResult.
    public Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default) =>
        new RabbitOperations().TestConnectionAsync(secret, ct);
}
