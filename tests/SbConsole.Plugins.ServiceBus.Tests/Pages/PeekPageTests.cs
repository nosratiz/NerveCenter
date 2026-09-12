using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
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

    public PeekPageTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        Services.AddSingleton(_connections);
        Services.AddSingleton(_operations);
        Services.AddSingleton<PeekMessagesQueryHandler>();
    }

    // Peek.razor's ConnectionId/DeadLetter are [SupplyParameterFromQuery] (they arrive as real
    // query-string values via Queues.razor's link, same as ConnectionName/IsProd already do for
    // that page) -- bUnit refuses ComponentParameterCollectionBuilder.Add() for those (it throws
    // telling you to navigate instead), so route through the fake NavigationManager the way
    // bUnit's own docs for testing [SupplyParameterFromQuery] components prescribe.
    private void NavigateToPeekQuery(Guid connectionId, bool deadLetter)
    {
        var navigationManager = Services.GetRequiredService<NavigationManager>();
        var uri = navigationManager.GetUriWithQueryParameters(new Dictionary<string, object?>
        {
            ["ConnectionId"] = connectionId,
            ["DeadLetter"] = deadLetter,
        });
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

        // Selecting the second message swaps the body pane to its content, proving OnSelect and
        // the MudList selection wiring actually work rather than just the auto-select default.
        cut.FindAll(".mud-list-item")[1].Click();

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
}
