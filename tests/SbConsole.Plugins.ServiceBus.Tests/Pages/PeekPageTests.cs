using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.Messages;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Pages;

public class PeekPageTests : BunitContext, IAsyncLifetime
{
    // See QueuesPageTests.cs: MudBlazor registers some interop-backed services that only
    // implement IAsyncDisposable, which xunit v2 only tears down via IAsyncLifetime.
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IServiceBusOperations _operations = Substitute.For<IServiceBusOperations>();
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly IConfirmationService _confirmation = Substitute.For<IConfirmationService>();

    public PeekPageTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        // Default seed: a non-prod connection. Individual tests (e.g. the prod-purge safety
        // test) override this via SeedConnection to exercise IsProd = true instead.
        SeedConnection(isProd: false);
        Services.AddSingleton(_connections);
        Services.AddSingleton(_operations);
        Services.AddLogging(); // handlers take an ILogger<T> so they can log the full exception behind a truncated UI message
        Services.AddSingleton<PeekMessagesQueryHandler>();
        Services.AddSingleton(_confirmation);
        Services.AddSingleton(new SbConsole.Plugins.ServiceBus.Messages.ResubmitDeadLetterMessagesCommandHandler(_operations, _connections, Substitute.For<IAuditScope>(), NullLogger<SbConsole.Plugins.ServiceBus.Messages.ResubmitDeadLetterMessagesCommandHandler>.Instance));
        Services.AddSingleton(new SbConsole.Plugins.ServiceBus.Messages.PurgeDeadLetterMessagesCommandHandler(_operations, _connections, Substitute.For<IAuditScope>(), NullLogger<SbConsole.Plugins.ServiceBus.Messages.PurgeDeadLetterMessagesCommandHandler>.Instance));
    }

    // Peek.razor no longer trusts ConnectionName/IsProd off the URL (that made the prod-purge
    // typed-confirmation gate trivially bypassable -- a hand-typed link omitting isProd silently
    // downgraded a prod purge to a plain confirm). It now looks the connection up from
    // IConnectionProvider by ConnectionId instead, so tests seed the connection through the
    // already-substituted provider rather than through the query string.
    private void SeedConnection(bool isProd, string name = "sb-conn")
    {
        var tags = isProd ? new[] { "prod" } : Array.Empty<string>();
        _connections.ListAsync("azure-servicebus", Arg.Any<CancellationToken>())
            .Returns(new List<ConnectionInfo> { new(_connectionId, name, "azure-servicebus", tags) });
    }

    // Peek.razor's ConnectionId/DeadLetter are [SupplyParameterFromQuery] (they arrive as real
    // query-string values via Queues.razor's link) -- bUnit refuses
    // ComponentParameterCollectionBuilder.Add() for those (it throws telling you to navigate
    // instead), so route through the fake NavigationManager the way bUnit's own docs for testing
    // [SupplyParameterFromQuery] components prescribe.
    private void NavigateToPeekQuery(Guid connectionId, bool deadLetter, long? deadLetterCount = null)
    {
        var navigationManager = Services.GetRequiredService<NavigationManager>();
        var queryParams = new Dictionary<string, object?>
        {
            ["ConnectionId"] = connectionId,
            ["DeadLetter"] = deadLetter,
        };
        if (deadLetterCount is not null)
        {
            queryParams["DeadLetterCount"] = deadLetterCount;
        }

        var uri = navigationManager.GetUriWithQueryParameters(queryParams);
        navigationManager.NavigateTo(uri);
    }

    [Fact]
    public async Task Shows_message_list_and_selecting_one_shows_its_body()
    {
        _operations.PeekMessagesAsync("Endpoint=sb://real", "orders-inbound", false, 32, null, Arg.Any<CancellationToken>())
            .Returns(new List<PeekedMessage>
            {
                new(1, """{"orderId":"UK-123"}""", "application/json", DateTimeOffset.UtcNow, 1, new Dictionary<string, string> { ["correlationId"] = "c-1" }),
                new(2, """{"orderId":"UK-456"}""", "application/json", DateTimeOffset.UtcNow, 1, new Dictionary<string, string> { ["correlationId"] = "c-2" }),
            });

        NavigateToPeekQuery(_connectionId, false);
        var cut = Render<SbConsole.Plugins.ServiceBus.Pages.Peek>(parameters => parameters
            .Add(p => p.QueueName, "orders-inbound"));
        await Task.Delay(30);
        cut.Render();

        // Auto-select-first-message behavior: the first message's body is shown without clicking.
        cut.Markup.Should().Contain("UK-123");
        cut.Markup.Should().Contain("correlationId");
        cut.Markup.Should().NotContain("UK-456");

        // Selecting the second message swaps the body pane to its content, proving the row click
        // wiring actually works rather than just the auto-select default. Queue mode (DeadLetter
        // false) renders no other buttons, so the two message rows are the only <button>s present.
        cut.FindAll("button")[1].Click();

        cut.Markup.Should().Contain("UK-456");
        cut.Markup.Should().Contain("c-2");
        cut.Markup.Should().NotContain("UK-123");
    }

    [Fact]
    public async Task Reloads_the_message_list_when_only_the_query_string_changes()
    {
        // Same route, same component instance (Blazor's normal same-route navigation behavior) --
        // only ConnectionId/DeadLetter in the query string change. OnInitializedAsync would not
        // re-run for this; OnParametersSetAsync must, so the message list can't go stale when
        // Task 9 links "peek" and "peek its dead-letter sub-queue" together.
        _operations.PeekMessagesAsync("Endpoint=sb://real", "orders-inbound", false, 32, null, Arg.Any<CancellationToken>())
            .Returns(new List<PeekedMessage>
            {
                new(1, "ACTIVE-BODY", "application/json", DateTimeOffset.UtcNow, 1, new Dictionary<string, string>()),
            });
        _operations.PeekMessagesAsync("Endpoint=sb://real", "orders-inbound", true, 32, null, Arg.Any<CancellationToken>())
            .Returns(new List<PeekedMessage>
            {
                new(2, "DEAD-LETTER-BODY", "application/json", DateTimeOffset.UtcNow, 5, new Dictionary<string, string>()),
            });

        NavigateToPeekQuery(_connectionId, false);
        var cut = Render<SbConsole.Plugins.ServiceBus.Pages.Peek>(parameters => parameters
            .Add(p => p.QueueName, "orders-inbound"));
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("ACTIVE-BODY");

        NavigateToPeekQuery(_connectionId, true);
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("DEAD-LETTER-BODY");
        cut.Markup.Should().NotContain("ACTIVE-BODY");
    }

    [Fact]
    public async Task Shows_a_snackbar_when_the_peek_fails()
    {
        _operations.PeekMessagesAsync("Endpoint=sb://real", "orders-inbound", false, 32, null, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<PeekedMessage>>(new InvalidOperationException("queue not found")));
        var snackbar = Services.GetRequiredService<ISnackbar>();

        NavigateToPeekQuery(_connectionId, false);
        var cut = Render<SbConsole.Plugins.ServiceBus.Pages.Peek>(parameters => parameters
            .Add(p => p.QueueName, "orders-inbound"));
        await Task.Delay(30);
        cut.Render();

        snackbar.ShownSnackbars.Should().Contain(s => s.Message != null && s.Message.Contains("queue not found"));
    }

    [Fact]
    public async Task Dead_letter_mode_shows_the_dead_letter_reason()
    {
        _operations.PeekMessagesAsync("Endpoint=sb://real", "orders-inbound", true, 32, null, Arg.Any<CancellationToken>())
            .Returns(new List<PeekedMessage>
            {
                new(1, "{}", "application/json", DateTimeOffset.UtcNow, 10, new Dictionary<string, string>(), "MaxDeliveryCountExceeded", "Handler threw"),
            });

        NavigateToPeekQuery(_connectionId, true);
        var cut = Render<SbConsole.Plugins.ServiceBus.Pages.Peek>(parameters => parameters
            .Add(p => p.QueueName, "orders-inbound"));
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("MaxDeliveryCountExceeded");
    }

    [Fact]
    public async Task Dead_letter_mode_shows_resubmit_and_purge_actions_but_queue_mode_does_not()
    {
        _operations.PeekMessagesAsync("Endpoint=sb://real", "orders-inbound", false, 32, null, Arg.Any<CancellationToken>())
            .Returns(new List<PeekedMessage>
            {
                new(1, "{}", "application/json", DateTimeOffset.UtcNow, 1, new Dictionary<string, string>()),
            });
        _operations.PeekMessagesAsync("Endpoint=sb://real", "orders-inbound", true, 32, null, Arg.Any<CancellationToken>())
            .Returns(new List<PeekedMessage> { new(1, "{}", "application/json", DateTimeOffset.UtcNow, 10, new Dictionary<string, string>()) });

        NavigateToPeekQuery(_connectionId, false);
        var queueModeCut = Render<SbConsole.Plugins.ServiceBus.Pages.Peek>(parameters => parameters
            .Add(p => p.QueueName, "orders-inbound"));
        await Task.Delay(30);
        queueModeCut.Render();

        queueModeCut.FindAll("button.purge-queue").Should().BeEmpty();
        queueModeCut.FindAll("input.select-message").Should().BeEmpty();

        NavigateToPeekQuery(_connectionId, true);
        var cut = Render<SbConsole.Plugins.ServiceBus.Pages.Peek>(parameters => parameters
            .Add(p => p.QueueName, "orders-inbound"));
        await Task.Delay(30);
        cut.Render();

        cut.FindAll("button.purge-queue").Should().HaveCount(1);
        cut.FindAll("input.select-message").Should().HaveCount(1);
    }

    [Fact]
    public async Task Resubmit_selected_calls_the_operations_seam_with_the_checked_sequence_numbers()
    {
        _operations.PeekMessagesAsync("Endpoint=sb://real", "orders-inbound", true, 32, null, Arg.Any<CancellationToken>())
            .Returns(new List<PeekedMessage> { new(1, "{}", "application/json", DateTimeOffset.UtcNow, 10, new Dictionary<string, string>()) });
        _operations.ResubmitDeadLetterMessagesAsync("Endpoint=sb://real", "orders-inbound", Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>())
            .Returns(1);

        NavigateToPeekQuery(_connectionId, true);
        var cut = Render<SbConsole.Plugins.ServiceBus.Pages.Peek>(parameters => parameters
            .Add(p => p.QueueName, "orders-inbound"));
        await Task.Delay(30);
        cut.Render();
        cut.Find("input.select-message").Change(true);
        cut.Find("button.resubmit-selected").Click();
        await Task.Delay(30);

        await _operations.Received(1).ResubmitDeadLetterMessagesAsync(
            "Endpoint=sb://real", "orders-inbound",
            Arg.Is<IReadOnlyList<long>>(l => l.SequenceEqual(new long[] { 1 })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Purge_goes_through_confirmation_before_calling_the_handler()
    {
        _operations.PeekMessagesAsync("Endpoint=sb://real", "orders-inbound", true, 32, null, Arg.Any<CancellationToken>())
            .Returns(new List<PeekedMessage> { new(1, "{}", "application/json", DateTimeOffset.UtcNow, 10, new Dictionary<string, string>()) });
        _confirmation.ConfirmAsync("Purge", "orders-inbound", false, Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(true);
        _operations.PurgeDeadLetterMessagesAsync("Endpoint=sb://real", "orders-inbound", Arg.Any<CancellationToken>()).Returns(1);

        NavigateToPeekQuery(_connectionId, true);
        var cut = Render<SbConsole.Plugins.ServiceBus.Pages.Peek>(parameters => parameters
            .Add(p => p.QueueName, "orders-inbound"));
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.purge-queue").Click();
        await Task.Delay(30);

        await _operations.Received(1).PurgeDeadLetterMessagesAsync("Endpoint=sb://real", "orders-inbound", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Purge_does_nothing_when_confirmation_is_denied()
    {
        _operations.PeekMessagesAsync("Endpoint=sb://real", "orders-inbound", true, 32, null, Arg.Any<CancellationToken>())
            .Returns(new List<PeekedMessage> { new(1, "{}", "application/json", DateTimeOffset.UtcNow, 10, new Dictionary<string, string>()) });
        // IConfirmationService.ConfirmAsync is left unstubbed for this call: NSubstitute defaults
        // an unstubbed Task<bool>-returning call to a completed task with result false, so this
        // exercises the "user declined" path without an explicit .Returns(false) (see the
        // analogous Delete_does_nothing_when_confirmation_is_denied test in QueuesPageTests.cs).

        NavigateToPeekQuery(_connectionId, true);
        var cut = Render<SbConsole.Plugins.ServiceBus.Pages.Peek>(parameters => parameters
            .Add(p => p.QueueName, "orders-inbound"));
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.purge-queue").Click();
        await Task.Delay(30);

        await _operations.DidNotReceive().PurgeDeadLetterMessagesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Purge_passes_the_connections_prod_flag_to_the_confirmation_prompt_not_a_url_parameter()
    {
        // Regression test for the original bug this task fixes: IsProd used to be
        // [SupplyParameterFromQuery], so a hand-typed URL that simply omitted isProd silently
        // downgraded a prod dead-letter purge from typed confirmation to a plain two-button
        // confirm -- the only reachable path to the purge button, since nothing in the app linked
        // to dead-letter mode. IsProd is now looked up from the connection record instead. Seeding
        // a prod-tagged connection and asserting ConfirmAsync receives isProd: true proves the
        // value is trusted from the connection, not the URL: NavigateToPeekQuery below carries no
        // isProd (or any prod-related) query parameter at all.
        SeedConnection(isProd: true);
        _operations.PeekMessagesAsync("Endpoint=sb://real", "orders-inbound", true, 32, null, Arg.Any<CancellationToken>())
            .Returns(new List<PeekedMessage> { new(1, "{}", "application/json", DateTimeOffset.UtcNow, 10, new Dictionary<string, string>()) });

        NavigateToPeekQuery(_connectionId, true);
        var cut = Render<SbConsole.Plugins.ServiceBus.Pages.Peek>(parameters => parameters
            .Add(p => p.QueueName, "orders-inbound"));
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.purge-queue").Click();
        await Task.Delay(30);

        await _confirmation.Received(1).ConfirmAsync("Purge", "orders-inbound", true, Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Purge_is_disabled_until_the_real_connection_record_has_resolved()
    {
        // The residual fail-open window left by the test above: the connection lookup in
        // OnParametersSetAsync is async, and until it resolves `_connection` is null, so
        // PurgeAsync's `_connection?.IsProd ?? false` would gate a prod purge behind a plain
        // two-button confirm. Holding the provider call open makes that window observable.
        var pendingLookup = new TaskCompletionSource<IReadOnlyList<ConnectionInfo>>();
        _connections.ListAsync("azure-servicebus", Arg.Any<CancellationToken>()).Returns(pendingLookup.Task);
        _operations.PeekMessagesAsync("Endpoint=sb://real", "orders-inbound", true, 32, null, Arg.Any<CancellationToken>())
            .Returns(new List<PeekedMessage> { new(1, "{}", "application/json", DateTimeOffset.UtcNow, 10, new Dictionary<string, string>()) });

        NavigateToPeekQuery(_connectionId, true);
        var cut = Render<SbConsole.Plugins.ServiceBus.Pages.Peek>(parameters => parameters
            .Add(p => p.QueueName, "orders-inbound"));
        await Task.Delay(30);
        cut.Render();

        cut.Find("button.purge-queue").HasAttribute("disabled").Should().BeTrue();

        pendingLookup.SetResult(new List<ConnectionInfo> { new(_connectionId, "sb-conn", "azure-servicebus", ["prod"]) });
        await Task.Delay(30);
        cut.Render();

        cut.Find("button.purge-queue").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public async Task Dead_letter_actions_are_disabled_and_show_a_spinner_while_a_purge_is_in_flight()
    {
        // Busy-state coverage for this page (see the convention note on Queues.razor). Purge and
        // resubmit share one flag here because they target the same sub-queue.
        _operations.PeekMessagesAsync("Endpoint=sb://real", "orders-inbound", true, 32, null, Arg.Any<CancellationToken>())
            .Returns(new List<PeekedMessage> { new(1, "{}", "application/json", DateTimeOffset.UtcNow, 10, new Dictionary<string, string>()) });
        _confirmation.ConfirmAsync("Purge", "orders-inbound", false, Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(true);
        var pendingPurge = new TaskCompletionSource<int>();
        _operations.PurgeDeadLetterMessagesAsync("Endpoint=sb://real", "orders-inbound", Arg.Any<CancellationToken>())
            .Returns(pendingPurge.Task);

        NavigateToPeekQuery(_connectionId, true);
        var cut = Render<SbConsole.Plugins.ServiceBus.Pages.Peek>(parameters => parameters
            .Add(p => p.QueueName, "orders-inbound"));
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.purge-queue").HasAttribute("disabled").Should().BeFalse();

        cut.Find("button.purge-queue").Click();
        await Task.Delay(30);
        cut.Render();

        cut.Find("button.purge-queue").HasAttribute("disabled").Should().BeTrue();
        cut.Find("button.resubmit-selected").HasAttribute("disabled").Should().BeTrue();
        cut.FindAll(".peek-busy").Should().NotBeEmpty();

        pendingPurge.SetResult(3);
        await Task.Delay(30);
        cut.Render();

        cut.Find("button.purge-queue").HasAttribute("disabled").Should().BeFalse();
        cut.FindAll(".peek-busy").Should().BeEmpty();
    }
}
