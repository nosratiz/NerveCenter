using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using NSubstitute;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Plugins.RabbitMq.Components;
using SbConsole.Sdk;

namespace SbConsole.Plugins.RabbitMq.Tests.Components;

public class ConnectionVhostPickerTests : RabbitPageTestBase
{
    private readonly List<RabbitSelection> _raised = [];

    private IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderPicker()
    {
        RenderFragment picker = builder =>
        {
            builder.OpenComponent<ConnectionVhostPicker>(0);
            builder.AddAttribute(1, nameof(ConnectionVhostPicker.SelectionChanged),
                EventCallback.Factory.Create<RabbitSelection>(this, s => _raised.Add(s)));
            builder.CloseComponent();
        };
        return RenderWithPopovers(picker);
    }

    [Fact]
    public async Task Defaults_to_the_first_connection_and_its_configured_vhost()
    {
        var cut = RenderPicker();
        await SettleAsync(cut);

        cut.Find(".selected-connection").TextContent.Should().Be("rabbit-dev");
        cut.Find(".selected-vhost").TextContent.Should().Be("/orders");
        _raised.Should().ContainSingle().Which.Should().Be(new RabbitSelection(Dev, "/orders"));
        cut.Find(".connection-echo").TextContent.Should().Contain("rabbit-dev");
        cut.Markup.Should().NotContain("password=");
    }

    [Fact]
    public async Task Honors_connectionId_and_vhost_from_the_query()
    {
        Navigation.NavigateTo($"/p/rabbitmq/overview?connectionId={Prod.Id}&vhost=%2Forders");

        var cut = RenderPicker();
        await SettleAsync(cut);

        cut.Find(".selected-connection").TextContent.Should().Be("rabbit-uk-prod");
        cut.Find(".prod-chip").TextContent.Should().Contain("prod");
        _raised.Should().ContainSingle().Which.Should().Be(new RabbitSelection(Prod, "/orders"));
    }

    [Fact]
    public async Task An_unknown_connectionId_falls_back_to_the_first_connection_and_its_default_vhost()
    {
        Navigation.NavigateTo($"/p/rabbitmq/overview?connectionId={Guid.NewGuid()}&vhost=%2Fnope");

        var cut = RenderPicker();
        await SettleAsync(cut);

        _raised.Should().ContainSingle().Which.Should().Be(new RabbitSelection(Dev, "/orders"));
    }

    [Fact]
    public async Task Switching_vhost_raises_SelectionChanged_and_rewrites_the_url_keeping_other_params()
    {
        Navigation.NavigateTo("/p/rabbitmq/overview?filter=pay");
        var cut = RenderPicker();
        await SettleAsync(cut);

        await OpenMenuAsync(cut, "vhost-menu");
        var root = cut.FindAll(".vhost-option").Single(o => o.TextContent.Contains("(default)"));
        await root.ClickAsync(new());
        await SettleAsync(cut);

        _raised.Last().Should().Be(new RabbitSelection(Dev, "/"));
        var uri = new Uri(Navigation.Uri);
        uri.Query.Should().Contain($"connectionId={Dev.Id}").And.Contain("vhost=%2F").And.Contain("filter=pay");
        uri.Query.Should().NotContain("%2Forders");
    }

    [Fact]
    public async Task Switching_connection_resets_the_vhost_to_that_connections_default()
    {
        var cut = RenderPicker();
        await SettleAsync(cut);

        await OpenMenuAsync(cut, "connection-menu");
        await cut.FindAll(".connection-option").Single(o => o.TextContent == "rabbit-uk-prod").ClickAsync(new());
        await SettleAsync(cut);

        _raised.Last().Should().Be(new RabbitSelection(Prod, "/billing"));
        Navigation.Uri.Should().Contain($"connectionId={Prod.Id}").And.Contain("vhost=%2Fbilling");
    }

    [Fact]
    public async Task A_failed_vhost_listing_offers_only_the_current_vhost()
    {
        Operations.ListVhostsAsync(DevSecret, Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<string>>(_ => throw new ManagementApiException(403, "GET", "/api/vhosts", "Access refused."));

        var cut = RenderPicker();
        await SettleAsync(cut);
        await OpenMenuAsync(cut, "vhost-menu");

        cut.FindAll(".vhost-option").Select(o => o.TextContent).Should().Equal("/orders");
        _raised.Should().ContainSingle().Which.Vhost.Should().Be("/orders");
    }

    [Fact]
    public async Task No_connections_shows_the_empty_state_and_never_raises()
    {
        ConnectionsProvider.ListAsync("rabbitmq", Arg.Any<CancellationToken>()).Returns(new List<ConnectionInfo>());

        var cut = RenderPicker();
        await SettleAsync(cut);

        cut.Markup.Should().Contain("No connections yet. Add a RabbitMQ connection to get started.");
        _raised.Should().BeEmpty();
    }
}
