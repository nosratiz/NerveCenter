using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.DeadLetter;
using SbConsole.Plugins.ServiceBus.Pages;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Pages;

public class DeadLetterOverviewPageTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IServiceBusOperations _operations = Substitute.For<IServiceBusOperations>();

    public DeadLetterOverviewPageTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_connections);
        Services.AddSingleton(_operations);
        Services.AddLogging();
        Services.AddSingleton<ListDeadLetterOverviewQueryHandler>();
    }

    [Fact]
    public async Task Shows_entries_with_peek_links_for_both_queue_and_subscription_rows()
    {
        var connectionId = Guid.NewGuid();
        _connections.ListAsync("azure-servicebus", Arg.Any<CancellationToken>())
            .Returns(new List<ConnectionInfo> { new(connectionId, "sb-dev", "azure-servicebus", []) });
        _connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        _operations.ListDeadLetterEntriesAsync("Endpoint=sb://real", Arg.Any<CancellationToken>())
            .Returns(new List<DeadLetterEntry>
            {
                new("Queue", null, "orders-inbound", 3),
                new("Subscription", "orders", "uk-team", 11),
            });

        var cut = Render<DeadLetterOverview>();
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("orders-inbound");
        cut.Markup.Should().Contain("uk-team");
        var links = cut.FindAll("a.peek-dead-letter");
        links.Should().HaveCount(2);
        links[0].GetAttribute("href").Should().Be($"/p/azure-servicebus/queues/orders-inbound/peek?connectionId={connectionId}&deadLetter=true&deadLetterCount=3");
        links[1].GetAttribute("href").Should().Be($"/p/azure-servicebus/topics/orders/subscriptions/uk-team/peek?connectionId={connectionId}&deadLetter=true&deadLetterCount=11");
    }

    [Fact]
    public async Task No_entries_shows_a_success_state()
    {
        _connections.ListAsync("azure-servicebus", Arg.Any<CancellationToken>()).Returns(new List<ConnectionInfo>());

        var cut = Render<DeadLetterOverview>();
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("No dead-lettered messages");
    }
}
