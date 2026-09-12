using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.Pages;
using SbConsole.Plugins.ServiceBus.Queues;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Pages;

public class QueuesPageTests : BunitContext, IAsyncLifetime
{
    // MudBlazor registers a few interop-backed services (key interception for popovers,
    // pointer-events routing) that implement only IAsyncDisposable. xunit v2 tears down a test
    // class via its synchronous IDisposable.Dispose() unless the class also implements
    // Xunit.IAsyncLifetime, in which case DisposeAsync() runs first -- disposing those services
    // the async-safe way before the base (synchronous) Dispose() runs as a no-op afterwards. See
    // ConnectionsPageTests.cs for the same pattern.
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IServiceBusOperations _operations = Substitute.For<IServiceBusOperations>();
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly ConnectionInfo _connectionInfo;

    public QueuesPageTests()
    {
        _connectionInfo = new ConnectionInfo(_connectionId, "sb-dev", "azure-servicebus", ["dev"]);
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _connections.ListAsync("azure-servicebus", Arg.Any<CancellationToken>())
            .Returns(new List<ConnectionInfo> { _connectionInfo });
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        Services.AddSingleton(_connections);
        Services.AddSingleton(_operations);
        Services.AddSingleton(Substitute.For<IAuditScope>());
        Services.AddSingleton(Substitute.For<IConfirmationService>());
        Services.AddSingleton<ListQueuesQueryHandler>();
        Services.AddSingleton<CreateQueueCommandHandler>();
        Services.AddSingleton<DeleteQueueCommandHandler>();
    }

    [Fact]
    public async Task Lists_queues_for_the_first_available_connection()
    {
        _operations.ListQueuesAsync("Endpoint=sb://real", Arg.Any<CancellationToken>())
            .Returns(new List<QueueSummary> { new("orders-inbound", 12, 3, 0, 2048) });

        var cut = Render<SbConsole.Plugins.ServiceBus.Pages.Queues>();
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("orders-inbound");
        cut.Markup.Should().Contain("12");
    }

    [Fact]
    public async Task No_connections_shows_an_honest_empty_state()
    {
        _connections.ListAsync("azure-servicebus", Arg.Any<CancellationToken>()).Returns(new List<ConnectionInfo>());

        var cut = Render<SbConsole.Plugins.ServiceBus.Pages.Queues>();
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("No connections");
    }

    [Fact]
    public async Task Dead_letter_link_points_at_the_peek_route_with_deadLetter_and_the_rows_live_count()
    {
        // Before this test, nothing in the app linked to dead-letter mode at all -- the
        // dead-letter browse/resubmit/purge feature was unreachable from the running app except
        // by hand-typing a URL. This proves the row action exists and carries connectionId,
        // deadLetter=true, and the row's live DeadLetterMessageCount (used for the purge
        // confirmation's message count, per Fix D) rather than connectionName/isProd (removed
        // per Fix A -- Peek.razor now looks those up itself from the connection record).
        _operations.ListQueuesAsync("Endpoint=sb://real", Arg.Any<CancellationToken>())
            .Returns(new List<QueueSummary> { new("orders-inbound", 12, 3, 0, 2048) });

        var cut = Render<SbConsole.Plugins.ServiceBus.Pages.Queues>();
        await Task.Delay(30);
        cut.Render();

        var link = cut.Find("a.dead-letter-action");
        link.GetAttribute("href").Should().Be(
            $"/p/azure-servicebus/queues/orders-inbound/peek?connectionId={_connectionId}&deadLetter=true&deadLetterCount=3");
    }

    [Fact]
    public async Task Delete_goes_through_confirmation_before_calling_the_handler()
    {
        _operations.ListQueuesAsync("Endpoint=sb://real", Arg.Any<CancellationToken>())
            .Returns(new List<QueueSummary> { new("orders-inbound", 0, 0, 0, 0) });
        var confirmation = Services.GetRequiredService<IConfirmationService>();
        confirmation.ConfirmAsync("Delete", "orders-inbound", _connectionInfo.IsProd, null, Arg.Any<CancellationToken>()).Returns(true);

        var cut = Render<SbConsole.Plugins.ServiceBus.Pages.Queues>();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.delete-queue").Click();
        await Task.Delay(30);

        // Asserting against the connection's actual IsProd (rather than a hard-coded literal)
        // means this fails if prod-gating stops flowing through to the confirmation call --
        // the seeded connection isn't tagged prod, so IsProd happens to be false, but a
        // hard-coded `false` here would hide a bug that skips passing it through entirely.
        await confirmation.Received(1).ConfirmAsync("Delete", "orders-inbound", _connectionInfo.IsProd, null, Arg.Any<CancellationToken>());
        await _operations.Received(1).DeleteQueueAsync("Endpoint=sb://real", "orders-inbound", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_does_nothing_when_confirmation_is_denied()
    {
        _operations.ListQueuesAsync("Endpoint=sb://real", Arg.Any<CancellationToken>())
            .Returns(new List<QueueSummary> { new("orders-inbound", 0, 0, 0, 0) });
        // IConfirmationService.ConfirmAsync is left unstubbed for this call: NSubstitute defaults
        // an unstubbed Task<bool>-returning call to a completed task with result false, so this
        // exercises the "user declined" path without an explicit .Returns(false).

        var cut = Render<SbConsole.Plugins.ServiceBus.Pages.Queues>();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.delete-queue").Click();
        await Task.Delay(30);

        await _operations.DidNotReceive().DeleteQueueAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
