using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Plugins.Redis.Overview;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis.Tests.Pages;

// Base class is BunitContext (not TestContext) and rendering goes through Render<T>, not
// RenderComponent<T> -- this bUnit version (2.10.3) marks both older names [Obsolete], which
// TreatWarningsAsErrors turns into a build failure. Same convention already used by every other
// plugin's page test base (see RabbitPageTestBase, and the Kafka/AWS page tests).
public abstract class RedisPageTestBase : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    protected readonly IRedisOperations Operations = Substitute.For<IRedisOperations>();
    protected readonly IConnectionProvider Connections = Substitute.For<IConnectionProvider>();
    protected readonly Guid ConnectionId = Guid.NewGuid();
    protected const string Secret = "standalone|0|localhost:6379";

    protected RedisPageTestBase()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;

        Connections.ListAsync("redis", Arg.Any<CancellationToken>()).Returns(
        [
            new ConnectionInfo(ConnectionId, "redis-uk-prod", "redis", ["prod"], true, DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow, null),
        ]);
        Connections.GetSecretAsync(ConnectionId, Arg.Any<CancellationToken>()).Returns(Secret);

        Services.AddSingleton(Operations);
        Services.AddSingleton(Connections);
        Services.AddSingleton(new GetOverviewQueryHandler(Operations, Connections, NullLogger<GetOverviewQueryHandler>.Instance));
        Services.AddSingleton(new GetMemoryByPrefixQueryHandler(Operations, Connections, NullLogger<GetMemoryByPrefixQueryHandler>.Instance));
        Services.AddSingleton(new GetSlowLogQueryHandler(Operations, Connections, NullLogger<GetSlowLogQueryHandler>.Instance));
    }

    protected static async Task SettleAsync<TComponent>(IRenderedComponent<TComponent> cut) where TComponent : IComponent
    {
        await Task.Delay(30);
        cut.Render();
    }
}
