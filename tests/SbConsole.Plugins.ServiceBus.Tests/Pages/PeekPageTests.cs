using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
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
            });

        NavigateToPeekQuery(_connectionId, false);
        var cut = Render<SbConsole.Plugins.ServiceBus.Pages.Peek>(parameters => parameters
            .Add(p => p.QueueName, "orders-inbound"));
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("UK-123");
        cut.Markup.Should().Contain("correlationId");
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
