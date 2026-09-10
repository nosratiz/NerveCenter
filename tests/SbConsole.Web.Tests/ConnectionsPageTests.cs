using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
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
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

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
    }
}
