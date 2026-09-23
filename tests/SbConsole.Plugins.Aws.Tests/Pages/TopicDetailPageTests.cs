using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Pages;
using SbConsole.Plugins.Aws.Subscriptions;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Pages;

public class TopicDetailPageTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    private readonly ISnsOperations _snsOperations = Substitute.For<ISnsOperations>();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly Guid _connectionId = Guid.NewGuid();

    public TopicDetailPageTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns("mode=access-keys;region=us-east-1");
        Services.AddSingleton(_connections);
        Services.AddSingleton(_snsOperations);
        Services.AddSingleton<ListSubscriptionsQueryHandler>();
        Services.AddLogging();
    }

    // [SupplyParameterFromQuery]-only properties can't be set via Render<T>(parameters => ...) --
    // must navigate with a real query string (established precedent: ServiceBus.Tests'
    // PeekPageTests.NavigateToPeekQuery). TopicArnEncoded is a plain (non-query) [Parameter], so
    // -- same as Peek.razor's QueueName in that same precedent -- it's set directly through the
    // Render<T> parameter builder rather than expected to resolve off the navigated URL: bUnit's
    // Render<T> doesn't run routing, so a route template's {TopicArnEncoded} segment is never
    // parsed out of NavigationManager's current URI on its own.
    [Fact]
    public void Renders_subscriptions_from_the_handler()
    {
        var topicArn = "arn:aws:sns:us-east-1:1:shipment-updates-topic";
        _snsOperations.ListSubscriptionsAsync("mode=access-keys;region=us-east-1", topicArn, Arg.Any<CancellationToken>())
            .Returns([new SubscriptionSummary("arn:sub-1", "sqs", "shipment-updates", false, false, null)]);

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"/p/aws/topics/{Uri.EscapeDataString(topicArn)}?connectionId={_connectionId}");
        var cut = Render<TopicDetail>(parameters => parameters
            .Add(p => p.TopicArnEncoded, Uri.EscapeDataString(topicArn)));

        cut.Markup.Should().Contain("shipment-updates");
        cut.Find("td.sub-state").TextContent.Should().Contain("Confirmed");
    }

    [Fact]
    public void Shows_pending_state_for_an_unconfirmed_subscription()
    {
        var topicArn = "arn:aws:sns:us-east-1:1:shipment-updates-topic";
        _snsOperations.ListSubscriptionsAsync(Arg.Any<string>(), topicArn, Arg.Any<CancellationToken>())
            .Returns([new SubscriptionSummary("PendingConfirmation", "email", "ops@example.com", true, null, null)]);

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"/p/aws/topics/{Uri.EscapeDataString(topicArn)}?connectionId={_connectionId}");
        var cut = Render<TopicDetail>(parameters => parameters
            .Add(p => p.TopicArnEncoded, Uri.EscapeDataString(topicArn)));

        cut.Find("td.sub-state").TextContent.Should().Contain("Pending");
    }
}
