using Microsoft.Extensions.DependencyInjection;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus;

public sealed class ServiceBusPlugin : IPlugin
{
    public string Id => "azure-servicebus";
    public string DisplayName => "Azure Service Bus";
    public string Version => "1.0.0";
    public IReadOnlyList<PluginNavItem> NavItems => [new("Queues", "/p/azure-servicebus/queues")];
    public string ConnectionKind => "azure-servicebus";
    public string ConnectionKindDisplayName => "Azure Service Bus";

    // Create/Delete queue, Peek, Send, Resubmit dead-letter, Purge dead-letter.
    public PluginContribution Contribution => new(PageCount: 1, ActionCount: 6);

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IServiceBusOperations, AzureServiceBusOperations>();
        services.AddScoped<Queues.ListQueuesQueryHandler>();
        services.AddScoped<Queues.CreateQueueCommandHandler>();
        services.AddScoped<Queues.DeleteQueueCommandHandler>();
    }

    // Plugins are constructed via a parameterless new() (AddSbConsolePlugin<TPlugin>()'s `new()`
    // constraint), so there's no DI container to pull a registered IServiceBusOperations from at
    // this layer — construct the real implementation directly, same as any other plugin would.
    public Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default) =>
        new AzureServiceBusOperations().TestConnectionAsync(secret, ct);
}
