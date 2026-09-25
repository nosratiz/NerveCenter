using System.Security.Authentication;
using FluentAssertions;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using SbConsole.Plugins.RabbitMq.Client;

namespace SbConsole.Plugins.RabbitMq.Tests.Client;

public class FriendlyRabbitErrorTests
{
    private static ShutdownEventArgs Reason(ushort code, string text = "raw broker text", ushort classId = 50) =>
        new(ShutdownInitiator.Peer, code, text, classId, 10);

    [Fact]
    public void ManagementApiException_message_names_method_path_and_status()
    {
        new ManagementApiException(403, "GET", "/api/exchanges/%2Forders", null).Message
            .Should().Be("GET /api/exchanges/%2Forders → 403");
    }

    [Fact]
    public void Management_401_maps_to_credentials_rejected()
    {
        FriendlyRabbitError.From(new ManagementApiException(401, "GET", "/api/overview", "Unauthorized"))
            .Should().Be("Management API rejected the credentials");
    }

    [Theory]
    [InlineData("User not authorised to access virtual host")]
    [InlineData("user not authorised to access Virtual Host")]
    public void Management_401_for_a_vhost_without_permissions_maps_to_the_permission_message(string reason)
    {
        // RabbitMQ 3.13 answers GET /api/exchanges/{vhost} for a vhost the user has no permission
        // on with 401 {"error":"not_authorised","reason":"User not authorised to access virtual
        // host"} -- the credentials are fine, so "rejected the credentials" would mislead.
        FriendlyRabbitError.From(new ManagementApiException(401, "GET", "/api/exchanges/%2F", reason))
            .Should().Be("User has no permission on this vhost (GET /api/exchanges/%2F → 401)");
    }

    [Fact]
    public void Management_403_names_the_missing_permission_or_tag()
    {
        FriendlyRabbitError.From(new ManagementApiException(403, "GET", "/api/queues/%2F", null))
            .Should().Be("User lacks permission or the monitoring/management tag for this vhost (GET /api/queues/%2F → 403)");
    }

    [Fact]
    public void Management_404_maps_to_not_found()
    {
        FriendlyRabbitError.From(new ManagementApiException(404, "GET", "/api/queues/%2F/q", null))
            .Should().Be("Not found (GET /api/queues/%2F/q → 404)");
    }

    [Theory]
    [InlineData(500)]
    [InlineData(503)]
    public void Management_5xx_maps_to_server_error(int code)
    {
        FriendlyRabbitError.From(new ManagementApiException(code, "PUT", "/api/queues/%2F/q", "boom"))
            .Should().Be($"Management API error {code} (PUT /api/queues/%2F/q)");
    }

    [Fact]
    public void Management_other_4xx_keeps_the_brokers_reason()
    {
        FriendlyRabbitError.From(new ManagementApiException(400, "PUT", "/api/queues/%2F/q", "inequivalent arg 'durable'"))
            .Should().Be("Management API rejected the request (PUT /api/queues/%2F/q → 400): inequivalent arg 'durable'");
    }

    [Fact]
    public void HttpRequestException_maps_to_management_unreachable_with_a_collapsed_message()
    {
        FriendlyRabbitError.From(new HttpRequestException("Connection refused\n  (localhost:15672)"))
            .Should().Be("Management API unreachable — Connection refused (localhost:15672)");
    }

    [Fact]
    public void HttpRequestException_from_a_tls_failure_says_so()
    {
        var ex = new HttpRequestException("The SSL connection could not be established, see inner exception.",
            new AuthenticationException("The remote certificate is invalid."));

        FriendlyRabbitError.From(ex).Should().Be("Management API TLS handshake failed — The remote certificate is invalid.");
    }

    [Fact]
    public void Timeouts_map_to_a_fixed_message()
    {
        FriendlyRabbitError.From(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"))
            .Should().Be("Timed out talking to the broker");
        FriendlyRabbitError.From(new TimeoutException("x")).Should().Be("Timed out talking to the broker");
    }

    [Fact]
    public void BrokerUnreachable_maps_to_amqp_unreachable()
    {
        FriendlyRabbitError.From(new BrokerUnreachableException(new System.Net.Sockets.SocketException()))
            .Should().Be("AMQP endpoint unreachable — check host, port and TLS");
    }

    [Fact]
    public void AuthenticationFailure_maps_to_credentials_rejected_directly_or_wrapped()
    {
        var auth = new AuthenticationFailureException("ACCESS_REFUSED - Login was refused");

        FriendlyRabbitError.From(auth).Should().Be("AMQP credentials rejected");
        FriendlyRabbitError.From(new BrokerUnreachableException(auth)).Should().Be("AMQP credentials rejected");
        FriendlyRabbitError.From(new BrokerUnreachableException(new PossibleAuthenticationFailureException("maybe")))
            .Should().Be("AMQP credentials rejected");
    }

    [Fact]
    public void Connection_level_access_refused_maps_to_credentials_rejected()
    {
        var ex = new OperationInterruptedException(Reason(403, "ACCESS_REFUSED - Login was refused", classId: 10));

        FriendlyRabbitError.From(ex).Should().Be("AMQP credentials rejected");
    }

    [Theory]
    [InlineData(405, "Resource locked — the queue has an exclusive consumer")]
    [InlineData(404, "Not found on the broker")]
    [InlineData(403, "Access refused by the broker")]
    public void OperationInterrupted_maps_by_reply_code(ushort code, string expected)
    {
        FriendlyRabbitError.From(new OperationInterruptedException(Reason(code))).Should().Be(expected);
    }

    [Fact]
    public void AlreadyClosed_with_a_known_reply_code_maps_like_OperationInterrupted()
    {
        FriendlyRabbitError.From(new AlreadyClosedException(Reason(404))).Should().Be("Not found on the broker");
    }

    [Fact]
    public void AlreadyClosed_otherwise_says_the_broker_closed_the_connection()
    {
        FriendlyRabbitError.From(new AlreadyClosedException(Reason(320, "CONNECTION_FORCED - broker forced connection closure")))
            .Should().Be("Broker closed the connection — CONNECTION_FORCED - broker forced connection closure");
    }

    [Fact]
    public void Missing_required_connection_key_passes_through_readably()
    {
        FriendlyRabbitError.From(new InvalidOperationException("Connection is missing 'host'."))
            .Should().Be("Connection is missing 'host'.");
    }
}
