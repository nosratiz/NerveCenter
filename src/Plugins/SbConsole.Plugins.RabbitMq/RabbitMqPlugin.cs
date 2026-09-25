using Microsoft.Extensions.DependencyInjection;
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

    // Empty for now: Task 3 registers IRabbitOperations and Task 5 the query/command handlers
    // (one scoped registration per handler class, same as AwsPlugin).
    public void ConfigureServices(IServiceCollection services)
    {
    }

    // Placeholder until Task 3 wires RabbitOperations' split AMQP/management test. Deliberately a
    // failed result rather than NotImplementedException: the host's connection editor must never
    // see an exception from this call.
    public Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default) =>
        Task.FromResult(new ConnectionTestResult(false, "Not implemented"));
}
