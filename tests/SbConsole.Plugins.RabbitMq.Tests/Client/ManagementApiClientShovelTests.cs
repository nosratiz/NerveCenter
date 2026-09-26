using FluentAssertions;
using SbConsole.Plugins.RabbitMq.Client;

namespace SbConsole.Plugins.RabbitMq.Tests.Client;

public class ManagementApiClientShovelTests
{
    [Fact]
    public async Task Running_shovel_merges_status_with_its_definition()
    {
        var shovels = await List(Fixtures.Read("shovels__2Forders.json"), Fixtures.Read("parameters_shovel__2Forders.json"));

        shovels.Single(s => s.Name == "legacy-migrate").Should().Be(new ShovelInfo(
            Name: "legacy-migrate",
            State: "running",
            Reason: null,
            SourceQueue: "legacy.import.q",
            SourceExchange: null,
            SourceUri: "amqp:///%2Forders",
            DestinationQueue: null,
            DestinationExchange: "order-events",
            DestinationRoutingKey: "order.legacy.created",
            DestinationUri: "amqp:///%2Forders",
            AckMode: "on-confirm",
            Timestamp: new DateTimeOffset(2026, 9, 25, 20, 11, 56, TimeSpan.Zero)));
    }

    [Fact]
    public async Task Definition_without_a_status_row_is_starting()
    {
        var shovels = await List("[]", Fixtures.Read("parameters_shovel__2Forders.json"));

        shovels.Should().HaveCount(2);
        var loop = shovels.Single(s => s.Name == "billing-retry-loop");
        loop.State.Should().Be("starting");
        loop.Timestamp.Should().BeNull();
        loop.SourceQueue.Should().Be("billing.retry");
        loop.DestinationExchange.Should().Be("billing.direct");
        loop.DestinationUri.Should().Be("amqp://rabbit-eu-dr.invalid:5672");
    }

    [Fact]
    public async Task Terminated_shovel_carries_its_reason()
    {
        const string status = """
            [{"node":"rabbit@rabbit-local","timestamp":"2026-09-25 20:13:14","name":"billing-retry-loop","vhost":"/orders",
              "type":"dynamic","state":"terminated","reason":"{failed_to_connect_using_provided_uris,nxdomain}"}]
            """;

        var shovels = await List(status, Fixtures.Read("parameters_shovel__2Forders.json"));

        var loop = shovels.Single(s => s.Name == "billing-retry-loop");
        loop.State.Should().Be("terminated");
        loop.Reason.Should().Be("{failed_to_connect_using_provided_uris,nxdomain}");
        loop.Timestamp.Should().Be(new DateTimeOffset(2026, 9, 25, 20, 13, 14, TimeSpan.Zero));
        loop.AckMode.Should().Be("on-confirm");
    }

    [Fact]
    public async Task Unrecognised_state_is_unknown_and_status_only_rows_are_kept()
    {
        const string status = """
            [{"name":"static-one","vhost":"/orders","type":"static","state":"weird",
              "src_uri":"amqp://a","src_exchange":"x","dest_uri":"amqp://b","dest_queue":"q"}]
            """;

        var shovel = (await List(status, "[]")).Single();

        shovel.State.Should().Be("unknown");
        shovel.SourceExchange.Should().Be("x");
        shovel.SourceUri.Should().Be("amqp://a");
        shovel.DestinationQueue.Should().Be("q");
    }

    [Fact]
    public async Task Missing_shovel_plugin_surfaces_as_404()
    {
        var (client, _) = TestSettings.Create(); // nothing routed -> the fake answers 404

        var act = () => client.ListShovelsAsync(TestSettings.Orders, "/orders");

        (await act.Should().ThrowAsync<ManagementApiException>()).Which.StatusCode.Should().Be(404);
    }

    private static async Task<IReadOnlyList<ShovelInfo>> List(string status, string definitions)
    {
        var (client, handler) = TestSettings.Create();
        handler.Respond("GET", "/api/shovels/%2Forders", status)
            .Respond("GET", "/api/parameters/shovel/%2Forders", definitions);
        return await client.ListShovelsAsync(TestSettings.Orders, "/orders");
    }
}
