using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.Sqlite.Backups;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Plugins.Sqlite.Overview;
using SbConsole.Plugins.Sqlite.Query;
using SbConsole.Plugins.Sqlite.Tables;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tests.Pages;

// BunitContext + Render<T> (not TestContext/RenderComponent): bUnit 2.10.3 marks the old names
// [Obsolete], which TreatWarningsAsErrors turns into a build failure (same as RedisPageTestBase).
public abstract class SqlitePageTestBase : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    protected const string Secret = "Path=/data/reports.db";
    protected const string ConnectionName = "sqlite-reports";
    protected readonly ISqliteOperations Operations = Substitute.For<ISqliteOperations>();
    protected readonly IConnectionProvider Connections = Substitute.For<IConnectionProvider>();
    protected readonly IAuditScope Audit = Substitute.For<IAuditScope>();
    protected readonly IConfirmationService Confirmation = Substitute.For<IConfirmationService>();
    protected readonly InMemoryPluginStore Store = new();
    protected readonly FakeTimeProvider Time = new(new DateTimeOffset(2026, 10, 13, 9, 40, 51, TimeSpan.Zero));
    protected readonly Guid ConnectionId = Guid.NewGuid();

    protected SqlitePageTestBase()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;

        Connections.ListAsync("sqlite", Arg.Any<CancellationToken>()).Returns(
        [
            new ConnectionInfo(ConnectionId, ConnectionName, "sqlite", ["prod"], true, Time.GetUtcNow(), null, Time.GetUtcNow(),
                new Dictionary<string, string> { ["File"] = "reports.db", ["Mode"] = "read-only" }),
        ]);
        Connections.GetSecretAsync(ConnectionId, Arg.Any<CancellationToken>()).Returns(Secret);

        Services.AddKeyedSingleton<IPluginStore>(SqlitePlugin.PluginId, Store);
        Services.AddSingleton<TimeProvider>(Time);
        Services.AddSingleton(Connections);
        Confirmation.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(true);
        Services.AddSingleton(Confirmation);
        Services.AddSingleton(new GetOverviewQueryHandler(Operations, Connections, NullLogger<GetOverviewQueryHandler>.Instance));
        Services.AddSingleton(new GetTableSizesQueryHandler(Operations, Connections, NullLogger<GetTableSizesQueryHandler>.Instance));
        Services.AddSingleton(new RunIntegrityCheckCommandHandler(Operations, Connections, Audit, Store, Time, NullLogger<RunIntegrityCheckCommandHandler>.Instance));
        Services.AddSingleton(new GetLastIntegrityResultQueryHandler(Store));
        Services.AddSingleton(new ListSchemaQueryHandler(Operations, Connections, NullLogger<ListSchemaQueryHandler>.Instance));
        Services.AddSingleton(new GetTableDetailQueryHandler(Operations, Connections, NullLogger<GetTableDetailQueryHandler>.Instance));
        Services.AddSingleton(new GetColumnSamplesQueryHandler(Operations, Connections, NullLogger<GetColumnSamplesQueryHandler>.Instance));
        Services.AddSingleton(new PreviewRowsQueryHandler(Operations, Connections, NullLogger<PreviewRowsQueryHandler>.Instance));
        Services.AddSingleton(new CountRowsQueryHandler(Operations, Connections, NullLogger<CountRowsQueryHandler>.Instance));
        Services.AddSingleton(new ExportTableCsvCommandHandler(Operations, Connections, Audit, NullLogger<ExportTableCsvCommandHandler>.Instance));
        Services.AddSingleton(new RunQueryQueryHandler(Operations, Connections, NullLogger<RunQueryQueryHandler>.Instance));
        Services.AddSingleton(new ExplainQueryQueryHandler(Operations, Connections, NullLogger<ExplainQueryQueryHandler>.Instance));
        Services.AddSingleton(new DryRunWriteQueryHandler(Operations, Connections, NullLogger<DryRunWriteQueryHandler>.Instance));
        Services.AddSingleton(new RunWriteCommandHandler(Operations, Connections, Audit, NullLogger<RunWriteCommandHandler>.Instance));
        Services.AddSingleton(new CheckpointWalCommandHandler(Operations, Connections, Audit, NullLogger<CheckpointWalCommandHandler>.Instance));
        Services.AddSingleton(new ExportQueryCsvCommandHandler(Operations, Connections, Audit, NullLogger<ExportQueryCsvCommandHandler>.Instance));
        var backup = new RunBackupCommandHandler(Operations, Connections, Audit, Store, Time, NullLogger<RunBackupCommandHandler>.Instance);
        Services.AddSingleton(backup);
        Services.AddSingleton(new CreateTableCommandHandler(Operations, Connections, Audit, NullLogger<CreateTableCommandHandler>.Instance));
        Services.AddSingleton(new RenameTableCommandHandler(Operations, Connections, Audit, NullLogger<RenameTableCommandHandler>.Instance));
        Services.AddSingleton(new GetDropImpactQueryHandler(Operations, Connections, NullLogger<GetDropImpactQueryHandler>.Instance));
        Services.AddSingleton(new DropTableCommandHandler(Operations, Connections, Audit, backup, NullLogger<DropTableCommandHandler>.Instance));
        Services.AddSingleton(new ReindexCommandHandler(Operations, Connections, Audit, Store, Time, NullLogger<ReindexCommandHandler>.Instance));
        Services.AddSingleton(new RecoverCommandHandler(Operations, Connections, Audit, Time, NullLogger<RecoverCommandHandler>.Instance));
        Services.AddSingleton(new GetBackupsQueryHandler(Operations, Connections, Store, Time, NullLogger<GetBackupsQueryHandler>.Instance));
        Services.AddSingleton(new GetGrowthQueryHandler(Operations, Connections, Store, Time, NullLogger<GetGrowthQueryHandler>.Instance));
        Services.AddSingleton(new VerifyBackupCommandHandler(Operations, Connections, Audit, Store, Time, NullLogger<VerifyBackupCommandHandler>.Instance));
        Services.AddSingleton(new PruneBackupsCommandHandler(Operations, Connections, Audit, Store, Time, NullLogger<PruneBackupsCommandHandler>.Instance));
        Services.AddSingleton(new DownloadBackupCommandHandler(Operations, Connections, Audit, Store, NullLogger<DownloadBackupCommandHandler>.Instance));
        Services.AddSingleton(new RestoreBackupCommandHandler(Operations, Connections, Audit, Store, Time, NullLogger<RestoreBackupCommandHandler>.Instance));
        Services.AddSingleton(new SaveBackupPolicyCommandHandler(Connections, Audit, Store, Time, NullLogger<SaveBackupPolicyCommandHandler>.Instance));
        Services.AddSingleton(new ListSavedQueriesQueryHandler(Store));
        Services.AddSingleton(new SaveQueryCommandHandler(Store, Time, NullLogger<SaveQueryCommandHandler>.Instance));
        Services.AddSingleton(new DeleteSavedQueryCommandHandler(Store, NullLogger<DeleteSavedQueryCommandHandler>.Instance));
    }

    /// <summary>MudMenu items render into a MudPopoverProvider (hosted by the real layout), so menu tests render one alongside.</summary>
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

    protected static async Task SettleAsync<TComponent>(IRenderedComponent<TComponent> cut) where TComponent : IComponent
    {
        await Task.Delay(30);
        cut.Render();
    }
}
