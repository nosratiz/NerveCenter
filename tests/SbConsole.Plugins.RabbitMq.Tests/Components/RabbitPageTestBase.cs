using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Plugins.RabbitMq.Connections;
using SbConsole.Plugins.RabbitMq.Exchanges;
using SbConsole.Plugins.RabbitMq.Messages;
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
    protected readonly IConfirmationService Confirmation = Substitute.For<IConfirmationService>();
    protected readonly IAuditScope Audit = Substitute.For<IAuditScope>();
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
        Services.AddSingleton(Audit);
        Services.AddSingleton(Confirmation);
        Services.AddLogging();
        Services.AddSingleton<ListVhostsQueryHandler>();
        Services.AddSingleton<GetConnectionEchoQueryHandler>();
        Services.AddSingleton<GetOverviewQueryHandler>();
        Services.AddSingleton<ListExchangesQueryHandler>();
        Services.AddSingleton<ListExchangeBindingsQueryHandler>();
        Services.AddSingleton<CreateExchangeCommandHandler>();
        Services.AddSingleton<DeleteExchangeCommandHandler>();
        Services.AddSingleton<PublishMessageCommandHandler>();
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

    // MudDialog only renders when it receives MudBlazor's internal IMudDialogInstanceInternal as a
    // cascading value, so the substitute implements both it and the public interface (same trick
    // as the AWS dialog tests), cascaded under its runtime proxy type.
    private static readonly Type MudDialogInstanceInternalType =
        typeof(IMudDialogInstance).Assembly.GetType("MudBlazor.IMudDialogInstanceInternal")
        ?? throw new InvalidOperationException("MudBlazor.IMudDialogInstanceInternal not found - MudBlazor API may have changed.");

    protected readonly IMudDialogInstance DialogInstance =
        (IMudDialogInstance)Substitute.For([typeof(IMudDialogInstance), MudDialogInstanceInternalType], []);

    /// <summary>Renders a dialog component inside the cascading dialog instance, with a popover provider for its selects.</summary>
    protected IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderDialog<TDialog>(IReadOnlyDictionary<string, object?> parameters)
        where TDialog : IComponent
    {
        var cascadingValueType = typeof(CascadingValue<>).MakeGenericType(DialogInstance.GetType());
        RenderFragment dialog = builder =>
        {
            builder.OpenComponent(0, cascadingValueType);
            builder.AddComponentParameter(1, "Value", DialogInstance);
            builder.AddComponentParameter(2, "IsFixed", true);
            builder.AddComponentParameter(3, "ChildContent", (RenderFragment)(inner =>
            {
                inner.OpenComponent<TDialog>(0);
                inner.AddMultipleAttributes(1, parameters.Select(p => new KeyValuePair<string, object>(p.Key, p.Value!)));
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        };

        return RenderWithPopovers(dialog);
    }

    public sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Utc;
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => Zone;
    }
}
