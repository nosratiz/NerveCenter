using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Plugins.RabbitMq.Connections;
using SbConsole.Plugins.RabbitMq.Overview;
using SbConsole.Sdk;

namespace SbConsole.Plugins.RabbitMq.Tests.Components;

/// <summary>
/// Shared bUnit setup for RabbitMQ pages: a substitute IRabbitOperations behind the real handlers
/// (same approach as the AWS page tests), two saved connections, and a settable clock.
/// </summary>
public abstract class RabbitPageTestBase : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    protected const string DevSecret = "host=rabbit-dev;vhost=%2Forders;username=u;password=p";
    protected const string ProdSecret = "host=rabbit-prod;vhost=%2Fbilling;username=u;password=s3cr3t";

    protected readonly IConnectionProvider ConnectionsProvider = Substitute.For<IConnectionProvider>();
    protected readonly IRabbitOperations Operations = Substitute.For<IRabbitOperations>();
    protected readonly ConnectionInfo Dev = new(Guid.NewGuid(), "rabbit-dev", "rabbitmq", ["dev"]);
    protected readonly ConnectionInfo Prod = new(Guid.NewGuid(), "rabbit-uk-prod", "rabbitmq", ["prod"]);
    protected readonly MutableClock Clock = new(new DateTimeOffset(2026, 9, 26, 14, 3, 7, TimeSpan.Zero));

    protected RabbitPageTestBase()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        ConnectionsProvider.ListAsync("rabbitmq", Arg.Any<CancellationToken>()).Returns(new List<ConnectionInfo> { Dev, Prod });
        ConnectionsProvider.GetSecretAsync(Dev.Id, Arg.Any<CancellationToken>()).Returns(DevSecret);
        ConnectionsProvider.GetSecretAsync(Prod.Id, Arg.Any<CancellationToken>()).Returns(ProdSecret);
        Operations.ListVhostsAsync(DevSecret, Arg.Any<CancellationToken>()).Returns(new List<string> { "/", "/orders" });
        Operations.ListVhostsAsync(ProdSecret, Arg.Any<CancellationToken>()).Returns(new List<string> { "/", "/billing", "/orders" });
        Services.AddSingleton(ConnectionsProvider);
        Services.AddSingleton(Operations);
        Services.AddSingleton(Substitute.For<IAuditScope>());
        Services.AddLogging();
        Services.AddSingleton<ListVhostsQueryHandler>();
        Services.AddSingleton<GetConnectionEchoQueryHandler>();
        Services.AddSingleton<GetOverviewQueryHandler>();
        Services.AddSingleton<TimeProvider>(Clock);
    }

    protected NavigationManager Navigation => Services.GetRequiredService<NavigationManager>();

    // MudMenu items render into <MudPopoverProvider/> (hosted by the real layout), so menu tests
    // render one alongside the component under test -- same as the AWS page tests.
    protected IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderWithPopovers(RenderFragment content)
    {
        RenderFragment fragment = builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.AddContent(1, content);
        };

        return Render(fragment);
    }

    protected static async Task SettleAsync(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut)
    {
        await Task.Delay(30);
        cut.Render();
    }

    protected static async Task OpenMenuAsync(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string menuClass)
    {
        await cut.Find($".{menuClass} .mud-menu-activator").KeyDownAsync(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });
        await SettleAsync(cut);
    }

    public sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Utc;
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => Zone;
    }
}
