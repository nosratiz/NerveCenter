using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Core.Audit;
using SbConsole.Core.Connections;
using SbConsole.Core.Security;
using SbConsole.Core.Tests;
using SbConsole.Sdk;
using SbConsole.Web.Components.Pages;

namespace SbConsole.Web.Tests;

public class ConnectionsPageTests : BunitContext, IAsyncLifetime
{
    // MudBlazor registers a few interop-backed services (key interception for popovers,
    // pointer-events routing) that implement only IAsyncDisposable. xunit v2 tears down a test
    // class via its synchronous IDisposable.Dispose() unless the class also implements
    // Xunit.IAsyncLifetime, in which case DisposeAsync() runs first -- disposing those services
    // the async-safe way before the base (synchronous) Dispose() runs as a no-op afterwards.
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        _testDb.Dispose();
    }

    private readonly TestDb _testDb = new();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private static readonly byte[] Key = new byte[32];

    public ConnectionsPageTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IEnumerable<IPlugin>>(Array.Empty<IPlugin>());
        Services.AddSingleton<Microsoft.EntityFrameworkCore.IDbContextFactory<SbConsole.Core.Data.SbcDbContext>>(_testDb);
        Services.AddSingleton(_audit);
        Services.AddSingleton<ISecretProtector>(new AesGcmSecretProtector(Key));
        Services.AddSingleton(new FakeTimeProvider());
        Services.AddSingleton<TimeProvider>(sp => sp.GetRequiredService<FakeTimeProvider>());
        Services.AddSingleton<CreateConnectionCommandHandler>();
        Services.AddSingleton<DeleteConnectionCommandHandler>();
        Services.AddSingleton<UpdateConnectionCommandHandler>();
        Services.AddSingleton<ListConnectionsQueryHandler>();
        Services.AddLogging();
        Services.AddSingleton<TestConnectionCommandHandler>();
        // Connections.razor injects IConfirmationService on every render; give every test a
        // default (tests that care about the confirm/cancel outcome override this before Render()).
        Services.AddSingleton(Substitute.For<SbConsole.Sdk.IConfirmationService>());
    }

    [Fact]
    public async Task Lists_seeded_connections()
    {
        await Services.GetRequiredService<CreateConnectionCommandHandler>()
            .HandleAsync(new CreateConnectionCommand("sb-dev", "azure-servicebus", "secret", ["dev"], "admin"));

        var cut = Render<Connections>();

        cut.Markup.Should().Contain("sb-dev");
    }

    [Fact]
    public async Task Delete_calls_the_handler_only_when_confirmation_service_returns_true()
    {
        // The default IConfirmationService substitute registered in the constructor must be
        // replaced BEFORE anything resolves from Services -- bUnit's container locks against
        // further registrations once a service has been retrieved from it (e.g. via
        // GetRequiredService, which the seeding call below does).
        var confirmation = Substitute.For<SbConsole.Sdk.IConfirmationService>();
        confirmation.ConfirmAsync("Delete", "sb-dev", false, null, Arg.Any<CancellationToken>()).Returns(true);
        Services.AddSingleton(confirmation);

        var created = await Services.GetRequiredService<CreateConnectionCommandHandler>()
            .HandleAsync(new CreateConnectionCommand("sb-dev", "azure-servicebus", "secret", ["dev"], "admin"));

        var cut = Render<Connections>();
        cut.Find("button.delete-connection").Click();
        await Task.Delay(50); // let the async click handler's awaits (confirm + DB delete) complete

        await confirmation.Received(1).ConfirmAsync("Delete", "sb-dev", false, null, Arg.Any<CancellationToken>());

        var remaining = await Services.GetRequiredService<ListConnectionsQueryHandler>().HandleAsync();
        remaining.Should().NotContain(c => c.Name == "sb-dev");
    }

    [Fact]
    public async Task Delete_leaves_the_connection_when_confirmation_service_returns_false()
    {
        // Same registration-ordering constraint as the confirmed-delete test above: replace the
        // default IConfirmationService substitute before anything resolves from Services.
        var confirmation = Substitute.For<SbConsole.Sdk.IConfirmationService>();
        confirmation.ConfirmAsync("Delete", "sb-dev", false, null, Arg.Any<CancellationToken>()).Returns(false);
        Services.AddSingleton(confirmation);

        await Services.GetRequiredService<CreateConnectionCommandHandler>()
            .HandleAsync(new CreateConnectionCommand("sb-dev", "azure-servicebus", "secret", ["dev"], "admin"));

        var cut = Render<Connections>();
        cut.Find("button.delete-connection").Click();
        await Task.Delay(50); // let the async click handler's awaits (confirm, short-circuited) complete

        await confirmation.Received(1).ConfirmAsync("Delete", "sb-dev", false, null, Arg.Any<CancellationToken>());

        var remaining = await Services.GetRequiredService<ListConnectionsQueryHandler>().HandleAsync();
        remaining.Should().Contain(c => c.Name == "sb-dev");
    }

    private sealed class FakeServiceBusPlugin(ConnectionTestResult result) : IPlugin
    {
        public string Id => "azure-servicebus";
        public string DisplayName => "Azure Service Bus";
        public string Version => "1.0.0";
        public IReadOnlyList<PluginNavItem> NavItems => [];
        public string ConnectionKind => "azure-servicebus";
        public string ConnectionKindDisplayName => "Azure Service Bus";
        public PluginContribution Contribution => new(0, 0);
        public void ConfigureServices(IServiceCollection services) { }
        public Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default) => Task.FromResult(result);
    }

    [Fact]
    public async Task Shows_never_tested_for_a_connection_that_has_no_recorded_test()
    {
        await Services.GetRequiredService<CreateConnectionCommandHandler>()
            .HandleAsync(new CreateConnectionCommand("sb-dev", "azure-servicebus", "secret", ["dev"], "admin"));

        var cut = Render<Connections>();

        cut.Markup.Should().Contain("Never tested");
    }

    [Fact]
    public async Task Test_button_runs_the_test_and_updates_the_status_to_ok()
    {
        Services.AddSingleton<IEnumerable<IPlugin>>([new FakeServiceBusPlugin(new ConnectionTestResult(true))]);
        Services.AddLogging();
        Services.AddSingleton<TestConnectionCommandHandler>();
        await Services.GetRequiredService<CreateConnectionCommandHandler>()
            .HandleAsync(new CreateConnectionCommand("sb-dev", "azure-servicebus", "secret", ["dev"], "admin"));

        var cut = Render<Connections>();
        cut.Find("button.test-connection").Click();
        await Task.Delay(50);
        cut.Render();

        cut.Markup.Should().Contain("OK");
        cut.Markup.Should().NotContain("Never tested");
    }

    [Fact]
    public async Task Test_button_shows_the_error_message_on_failure()
    {
        Services.AddSingleton<IEnumerable<IPlugin>>([new FakeServiceBusPlugin(new ConnectionTestResult(false, "Unauthorized (401)"))]);
        Services.AddLogging();
        Services.AddSingleton<TestConnectionCommandHandler>();
        await Services.GetRequiredService<CreateConnectionCommandHandler>()
            .HandleAsync(new CreateConnectionCommand("sb-dev", "azure-servicebus", "secret", ["dev"], "admin"));

        var cut = Render<Connections>();
        cut.Find("button.test-connection").Click();
        await Task.Delay(50);
        cut.Render();

        cut.Markup.Should().Contain("Unauthorized (401)");
    }
}
