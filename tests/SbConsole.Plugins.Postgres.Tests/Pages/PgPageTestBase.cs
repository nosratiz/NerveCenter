using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.Postgres.Client;
using SbConsole.Plugins.Postgres.Overview;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Postgres.Tests.Pages;

// BunitContext + Render<T> (not TestContext/RenderComponent): bUnit 2.10.3 marks the old names
// [Obsolete], which TreatWarningsAsErrors turns into a build failure. Later page tasks extend this
// base by registering their handlers in RegisterHandlers().
public abstract class PgPageTestBase : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    protected const string ConnectionName = "pg-prod-eu";
    protected static readonly string Secret = new PostgresConnectionSettings("db", Mode: PgAccessMode.ReadWrite).ToSecretString();

    protected readonly IPostgresOperations Operations = Substitute.For<IPostgresOperations>();
    protected readonly IConnectionProvider Connections = Substitute.For<IConnectionProvider>();
    protected readonly IAuditScope Audit = Substitute.For<IAuditScope>();
    protected readonly IConfirmationService Confirmation = Substitute.For<IConfirmationService>();
    protected readonly InMemoryPluginStore Store = new();
    protected readonly FakeTimeProvider Time = new(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));
    protected readonly Guid ConnectionId = Guid.NewGuid();

    protected PgPageTestBase()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;

        Connections.ListAsync("postgres", Arg.Any<CancellationToken>()).Returns(
        [
            new ConnectionInfo(ConnectionId, ConnectionName, "postgres", ["prod"], true, Time.GetUtcNow(), null, Time.GetUtcNow(),
                new Dictionary<string, string> { ["Server"] = "db:5432", ["Database"] = "app", ["Mode"] = "read-write" }),
        ]);
        Connections.GetSecretAsync(ConnectionId, Arg.Any<CancellationToken>()).Returns(Secret);

        Services.AddKeyedSingleton<IPluginStore>(PostgresPlugin.PluginId, Store);
        Services.AddSingleton<TimeProvider>(Time);
        Services.AddSingleton(Connections);
        Services.AddSingleton(Audit);
        Services.AddSingleton(Confirmation);
        Services.AddSingleton(Operations);
        RegisterHandlers();
    }

    private void RegisterHandlers()
    {
        Services.AddSingleton(new GetOverviewQueryHandler(
            Operations, Connections, Store, Time, NullLogger<GetOverviewQueryHandler>.Instance));
        Services.AddSingleton(new ListDatabasesQueryHandler(
            Operations, Connections, NullLogger<ListDatabasesQueryHandler>.Instance));
    }

    /// <summary>MudMenu/MudSelect items render into a MudPopoverProvider (hosted by the real layout), so such tests render one alongside.</summary>
    protected IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderWithPopovers(RenderFragment content)
    {
        RenderFragment fragment = builder =>
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.AddContent(1, content);
        };

        return Render(fragment);
    }
}
