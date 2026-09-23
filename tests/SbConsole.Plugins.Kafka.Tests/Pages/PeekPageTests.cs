using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
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
    public async Task Changing_the_start_mode_after_a_fetch_hides_the_now_stale_watermark_caption()
    {
        _operations.PeekMessagesAsync("bootstrap.servers=real:9092", "orders", 0, PeekStart.Latest, null, 32, Arg.Any<CancellationToken>())
            .Returns(new PeekResult(new List<KafkaMessageSummary> { new(0, 5, DateTimeOffset.UtcNow, "k1", "hello", false) }, 100, 205));

        NavigateToPeekQuery(_connectionId);

        // MudSelect's dropdown content is rendered by <MudPopoverProvider/>, a separate component
        // the real app hosts once in its layout -- bUnit only renders what's given to Render(...),
        // so a lone <Peek/> never gets the popover's <div class="mud-list-item"> markup in its
        // subtree. Same pattern SbConsole.Web.Tests/ConnectionEditorTests.cs uses.
        RenderFragment fragment = builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<SbConsole.Plugins.Kafka.Pages.Peek>(1);
            builder.AddComponentParameter(2, nameof(SbConsole.Plugins.Kafka.Pages.Peek.TopicName), "orders");
            builder.CloseComponent();
        };
        var cut = Render(fragment);

        cut.Find("button.fetch-messages").Click();
        await Task.Delay(30);
        cut.Render();
        cut.FindAll(".partition-watermarks").Should().ContainSingle();

        // Open the "Start from" select (the second MudSelect on the page) and pick a different
        // option without clicking Fetch again -- the caption belongs to the fetch that just ran
        // and must disappear rather than keep showing a now-stale pairing.
        cut.FindAll("div.mud-input-control.mud-select")[1].MouseDown(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        await Task.Delay(30);
        cut.Render();
        cut.FindAll("div.mud-list-item").First(item => item.TextContent.Contains("Offset")).Click();
        await Task.Delay(30);
        cut.Render();

        cut.FindAll(".partition-watermarks").Should().BeEmpty();
    }

    [Fact]
    public async Task Partition_select_labels_each_option_with_its_own_partition_index()
    {
        // PartitionCount rides the query string (Topics.razor's PeekUrl puts it there), so it has
        // to be navigated in rather than passed as a render parameter -- same split as ConnectionId.
        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo(navigationManager.GetUriWithQueryParameters(
            new Dictionary<string, object?> { ["ConnectionId"] = _connectionId, ["PartitionCount"] = 3 }));

        // MudPopoverProvider for the same reason the start-mode test above needs it: a MudSelect's
        // options are rendered by the provider, not inside <Peek/>'s own subtree.
        RenderFragment fragment = builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<SbConsole.Plugins.Kafka.Pages.Peek>(1);
            builder.AddComponentParameter(2, nameof(SbConsole.Plugins.Kafka.Pages.Peek.TopicName), "orders");
            builder.CloseComponent();
        };
        var cut = Render(fragment);

        // Open the "Partition" select -- the first MudSelect on the page.
        cut.FindAll("div.mud-input-control.mud-select")[0].MouseDown(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        await Task.Delay(30);
        cut.Render();

        // Only the numeric options belong to this select; "Start from" contributes word labels.
        var partitionLabels = cut.FindAll("div.mud-list-item")
            .Select(item => item.TextContent.Trim())
            .Where(text => text.Length > 0 && text.All(char.IsDigit))
            .ToList();

        partitionLabels.Should().Equal("0", "1", "2");
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
