using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Plugins.Kafka.Messages;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.Tests.Pages;

public class PeekPageTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IKafkaOperations _operations = Substitute.For<IKafkaOperations>();
    private readonly Guid _connectionId = Guid.NewGuid();

    public PeekPageTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns("bootstrap.servers=real:9092");
        Services.AddSingleton(_connections);
        Services.AddSingleton(_operations);
        Services.AddLogging();
        Services.AddSingleton<PeekMessagesQueryHandler>();
    }

    // TopicName is a route parameter (set via Render's parameter builder); ConnectionId is
    // [SupplyParameterFromQuery], so it's carried through real URL navigation instead -- same split
    // SubscriptionPeekPageTests.cs (SbConsole.Plugins.ServiceBus.Tests) uses for its own
    // route-vs-query parameters.
    private void NavigateToPeekQuery(Guid connectionId)
    {
        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo(navigationManager.GetUriWithQueryParameters(
            new Dictionary<string, object?> { ["ConnectionId"] = connectionId }));
    }

    private IRenderedComponent<SbConsole.Plugins.Kafka.Pages.Peek> RenderPage() =>
        Render<SbConsole.Plugins.Kafka.Pages.Peek>(parameters => parameters
            .Add(p => p.TopicName, "orders"));

    [Fact]
    public async Task Fetches_and_renders_messages_and_watermarks_for_the_selected_partition()
    {
        // _start defaults to PeekStart.Latest in Peek.razor -- match that default rather than
        // Earliest, since Fetch is clicked with no prior interaction with the "Start from" select.
        // maxMessages is a literal (32, Peek.razor's default), not Arg.Any<int>() -- partition is
        // also an int, and NSubstitute can't disambiguate a literal from a matcher of the same
        // underlying type in one call.
        _operations.PeekMessagesAsync("bootstrap.servers=real:9092", "orders", 0, PeekStart.Latest, null, 32, Arg.Any<CancellationToken>())
            .Returns(new PeekResult(new List<KafkaMessageSummary> { new(0, 5, DateTimeOffset.UtcNow, "k1", "hello", false) }, 100, 205));

        NavigateToPeekQuery(_connectionId);
        var cut = RenderPage();
        cut.Find("button.fetch-messages").Click();
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("hello");
        cut.Markup.Should().Contain("low 100");
        cut.Markup.Should().Contain("high 205");
    }

    [Fact]
    public async Task Watermark_display_is_hidden_before_the_first_fetch()
    {
        NavigateToPeekQuery(_connectionId);
        var cut = RenderPage();

        cut.FindAll(".partition-watermarks").Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_fetch_after_a_successful_one_keeps_the_previous_watermarks_displayed()
    {
        var successResult = new PeekResult(new List<KafkaMessageSummary> { new(0, 5, DateTimeOffset.UtcNow, "k1", "hello", false) }, 100, 205);
        _operations.PeekMessagesAsync("bootstrap.servers=real:9092", "orders", 0, PeekStart.Latest, null, 32, Arg.Any<CancellationToken>())
            .Returns(
                _ => successResult,
                _ => throw new InvalidOperationException("boom"));

        NavigateToPeekQuery(_connectionId);
        var cut = RenderPage();
        cut.Find("button.fetch-messages").Click();
        await Task.Delay(30);
        cut.Render();
        cut.Markup.Should().Contain("low 100");

        cut.Find("button.fetch-messages").Click();
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("low 100");
        cut.Markup.Should().Contain("high 205");
    }
}
