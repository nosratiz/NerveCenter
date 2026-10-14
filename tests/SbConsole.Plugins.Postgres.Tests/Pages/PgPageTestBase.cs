using Bunit;
using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.Postgres.Activity;
using SbConsole.Plugins.Postgres.Browse;
using SbConsole.Plugins.Postgres.Client;
using SbConsole.Plugins.Postgres.Maintenance;
using SbConsole.Plugins.Postgres.Overview;
using SbConsole.Plugins.Postgres.Query;
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
        Confirmation.ConfirmAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>()).Returns(true);
        RegisterHandlers();
    }

    private void RegisterHandlers()
    {
        Services.AddSingleton(new GetOverviewQueryHandler(
            Operations, Connections, Store, Time, NullLogger<GetOverviewQueryHandler>.Instance));
        Services.AddSingleton(new ListDatabasesQueryHandler(
            Operations, Connections, NullLogger<ListDatabasesQueryHandler>.Instance));
        Services.AddSingleton(new ListSchemaQueryHandler(Operations, Connections, NullLogger<ListSchemaQueryHandler>.Instance));
        Services.AddSingleton(new GetObjectDetailQueryHandler(Operations, Connections, NullLogger<GetObjectDetailQueryHandler>.Instance));
        Services.AddSingleton(new GetDdlQueryHandler(Operations, Connections, NullLogger<GetDdlQueryHandler>.Instance));
        Services.AddSingleton(new PreviewRowsQueryHandler(Operations, Connections, NullLogger<PreviewRowsQueryHandler>.Instance));
        Services.AddSingleton(new ExportTableCsvCommandHandler(Operations, Connections, Audit, NullLogger<ExportTableCsvCommandHandler>.Instance));
        Services.AddSingleton(new RunQueryCommandHandler(Operations, Connections, Audit, Store, Time, NullLogger<RunQueryCommandHandler>.Instance));
        Services.AddSingleton(new ExplainQueryQueryHandler(Operations, Connections, NullLogger<ExplainQueryQueryHandler>.Instance));
        Services.AddSingleton(new GetCompletionSchemaQueryHandler(Operations, Connections, NullLogger<GetCompletionSchemaQueryHandler>.Instance));
        Services.AddSingleton(new ExportQueryCsvCommandHandler(Operations, Connections, Audit, NullLogger<ExportQueryCsvCommandHandler>.Instance));
        Services.AddSingleton(new ListQueryHistoryQueryHandler(Store, Time));
        Services.AddSingleton(new GetActivityQueryHandler(Operations, Connections, NullLogger<GetActivityQueryHandler>.Instance));
        Services.AddSingleton(new CancelBackendCommandHandler(Operations, Connections, Audit, NullLogger<CancelBackendCommandHandler>.Instance));
        Services.AddSingleton(new TerminateBackendCommandHandler(Operations, Connections, Audit, NullLogger<TerminateBackendCommandHandler>.Instance));
        Services.AddSingleton(new ReindexIndexCommandHandler(Operations, Connections, Audit, NullLogger<ReindexIndexCommandHandler>.Instance));
        Services.AddSingleton(new DropIndexCommandHandler(Operations, Connections, Audit, NullLogger<DropIndexCommandHandler>.Instance));
        Services.AddSingleton(new GetMaintenanceQueryHandler(Operations, Connections, NullLogger<GetMaintenanceQueryHandler>.Instance));
        Services.AddSingleton(new RunMaintenanceCommandHandler(Operations, Connections, Audit, NullLogger<RunMaintenanceCommandHandler>.Instance));
        Services.AddSingleton<AuthenticationStateProvider>(new FakeAuthStateProvider());
    }

    private sealed class FakeAuthStateProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], "test"))));
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
