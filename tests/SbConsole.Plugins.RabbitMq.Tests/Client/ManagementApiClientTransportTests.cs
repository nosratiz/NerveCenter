using System.Net;
using System.Text;
using FluentAssertions;
using SbConsole.Plugins.RabbitMq.Client;

namespace SbConsole.Plugins.RabbitMq.Tests.Client;

public class ManagementApiClientTransportTests
{
    [Fact]
    public async Task Every_request_carries_basic_auth_and_accepts_json()
    {
        var (client, handler) = TestSettings.Create();
        handler.RespondFixture("/api/vhosts", "vhosts.json");

        await client.ListVhostsAsync(TestSettings.Orders);

        var request = handler.Requests.Should().ContainSingle().Subject;
        var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes("sbconsole:s:crét"));
        request.Authorization.Should().Be($"Basic {expected}");
        request.Accept.Should().Be("application/json");
    }

    [Fact]
    public async Task Writes_send_basic_auth_but_no_accept_header()
    {
        // The purge endpoint answers 406 to "Accept: application/json" (verified on 3.13.7).
        var (client, handler) = TestSettings.Create();
        handler.Respond("DELETE", "/api/queues/%2Forders/q1/contents", "", HttpStatusCode.NoContent);

        await client.PurgeQueueAsync(TestSettings.Orders, "/orders", "q1");

        var request = handler.Requests.Single();
        request.Authorization.Should().StartWith("Basic ");
        request.Accept.Should().BeEmpty();
    }

    [Fact]
    public async Task Vhost_and_names_are_escaped_into_path_segments_and_keep_percent_2F()
    {
        var (client, handler) = TestSettings.Create();
        handler.Respond("GET", "/api/queues/%2Forders/odd%2Fname%20q", Fixtures.Read("queues__2Forders_order-events.q.json"));
        handler.Respond("GET", "/api/queues/%2Forders/odd%2Fname%20q/bindings", "[]");

        await client.GetQueueAsync(TestSettings.Orders, "/orders", "odd/name q");

        handler.Requests[0].Uri.AbsoluteUri.Should().Be("http://localhost:15672/api/queues/%2Forders/odd%2Fname%20q");
        handler.Requests[0].Uri.OriginalString.Should().Contain("/api/queues/%2Forders/odd%2Fname%20q");
    }

    [Fact]
    public async Task Requests_resolve_under_a_path_prefixed_management_url()
    {
        var handler = new FakeManagementHandler().RespondFixture("/rabbit/api/vhosts", "vhosts.json");
        var client = new ManagementApiClient(_ => handler);
        var settings = RabbitConnectionSettings.From(
            "host=h;username=u;password=p;managementUrl=https%3A%2F%2Fmgmt.example%2Frabbit");

        var vhosts = await client.ListVhostsAsync(settings);

        vhosts.Should().Equal("/", "/orders");
        handler.Requests.Single().Uri.AbsoluteUri.Should().Be("https://mgmt.example/rabbit/api/vhosts");
    }

    [Fact]
    public async Task Non_success_throws_ManagementApiException_with_the_broker_reason()
    {
        var (client, _) = TestSettings.Create();

        var act = () => client.GetQueueAsync(TestSettings.Orders, "/orders", "missing");

        var ex = (await act.Should().ThrowAsync<ManagementApiException>()).Which;
        ex.StatusCode.Should().Be(404);
        ex.Method.Should().Be("GET");
        ex.Path.Should().Be("/api/queues/%2Forders/missing");
        ex.Reason.Should().Be("Not Found");
    }

    [Fact]
    public async Task Non_json_error_body_leaves_reason_null()
    {
        var (client, handler) = TestSettings.Create();
        handler.Respond("GET", "/api/overview", "<html>bad gateway</html>", HttpStatusCode.BadGateway);

        var act = () => client.GetOverviewAsync(TestSettings.Orders);

        var ex = (await act.Should().ThrowAsync<ManagementApiException>()).Which;
        ex.StatusCode.Should().Be(502);
        ex.Reason.Should().BeNull();
    }

    [Fact]
    public async Task Write_error_carries_method_and_reason()
    {
        var (client, handler) = TestSettings.Create();
        handler.Respond("PUT", "/api/queues/%2Forders/q1",
            """{"error":"bad_request","reason":"inequivalent arg 'x-queue-type' for queue 'q1'"}""", HttpStatusCode.BadRequest);

        var act = () => client.CreateQueueAsync(TestSettings.Orders, "/orders", new CreateQueueRequest("q1"));

        var ex = (await act.Should().ThrowAsync<ManagementApiException>()).Which;
        ex.StatusCode.Should().Be(400);
        ex.Method.Should().Be("PUT");
        ex.Reason.Should().Be("inequivalent arg 'x-queue-type' for queue 'q1'");
    }

    [Fact]
    public async Task Probe_reads_version_tags_and_vhost_counts()
    {
        var (client, handler) = TestSettings.Create();
        handler.RespondFixture("/api/overview", "overview.json")
            .RespondFixture("/api/whoami", "whoami.json")
            .RespondFixture("/api/exchanges/%2Forders?columns=name", "exchanges__2Forders.json")
            .RespondFixture("/api/queues/%2Forders?columns=name", "queues__2Forders.json");

        var probe = await client.ProbeAsync(TestSettings.Orders);

        probe.Should().Be(probe with
        {
            RabbitVersion = "3.13.7",
            ClusterName = "rabbit@local",
            ExchangeCount = 15,
            QueueCount = 10,
        });
        probe.UserTags.Should().Equal("management", "monitoring", "policymaker");
    }

    [Fact]
    public async Task Probe_accepts_legacy_comma_separated_tags()
    {
        var (client, handler) = TestSettings.Create();
        handler.RespondFixture("/api/overview", "overview.json")
            .Respond("GET", "/api/whoami", """{"name":"sbconsole","tags":"administrator,monitoring"}""")
            .Respond("GET", "/api/exchanges/%2Forders?columns=name", "[]")
            .Respond("GET", "/api/queues/%2Forders?columns=name", "[]");

        var probe = await client.ProbeAsync(TestSettings.Orders);

        probe.UserTags.Should().Equal("administrator", "monitoring");
        probe.ExchangeCount.Should().Be(0);
    }

    [Fact]
    public void Default_handler_is_shared_per_management_url_and_verify_flag()
    {
        var a = RabbitConnectionSettings.From("host=a.example;username=u;password=p");
        var aOtherUser = RabbitConnectionSettings.From("host=a.example;username=v;password=q");
        var aUnverified = RabbitConnectionSettings.From("host=a.example;username=u;password=p;verifyCert=false");

        var first = ManagementApiClient.DefaultHandlerFactory(a);

        ManagementApiClient.DefaultHandlerFactory(aOtherUser).Should().BeSameAs(first);
        ManagementApiClient.DefaultHandlerFactory(aUnverified).Should().NotBeSameAs(first);
    }
}
