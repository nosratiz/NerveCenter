using System.Net;
using FluentAssertions;
using SbConsole.Plugins.RabbitMq.Client;

namespace SbConsole.Plugins.RabbitMq.Tests.Client;

/// <summary>
/// Path-segment safety (.NET's Uri removes "." / ".." segments, so a queue named "." once turned a
/// purge into DELETE /api/queues/%2F/contents) and URI-userinfo redaction of broker error reasons.
/// </summary>
public class ManagementApiClientSafetyTests
{
    public static TheoryData<string> DotNames => new() { ".", ".." };

    [Theory]
    [MemberData(nameof(DotNames))]
    public async Task Dot_segment_names_are_rejected_before_any_request_is_sent(string name)
    {
        var (client, handler) = TestSettings.Create();
        var s = TestSettings.Orders;
        var calls = new Func<Task>[]
        {
            () => client.PurgeQueueAsync(s, "/orders", name),
            () => client.DeleteQueueAsync(s, "/orders", name),
            () => client.GetQueueAsync(s, "/orders", name),
            () => client.CreateQueueAsync(s, "/orders", new CreateQueueRequest(name, "classic")),
            () => client.DeleteExchangeAsync(s, "/orders", name),
            () => client.CreateExchangeAsync(s, "/orders", new CreateExchangeRequest(name, "topic")),
            () => client.AddBindingAsync(s, "/orders", name, "q", "k", new Dictionary<string, object?>()),
            () => client.AddBindingAsync(s, "/orders", "ex", name, "k", new Dictionary<string, object?>()),
            () => client.RemoveBindingAsync(s, "/orders", "ex", "q", name),
            () => client.DeleteShovelAsync(s, "/orders", name),
            () => client.RestartShovelAsync(s, "/orders", name),
            () => client.ListQueuesAsync(s, name),
            () => client.ListExchangesAsync(s, name),
        };

        foreach (var call in calls)
        {
            await call.Should().ThrowAsync<ArgumentException>();
        }

        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public void The_dot_segment_error_is_passed_through_as_the_friendly_text()
    {
        var ex = ManagementApiClient.DotSegmentError(".");

        FriendlyRabbitError.From(ex).Should().Contain("\".\"").And.Contain("management API");
    }

    [Fact]
    public async Task Names_merely_containing_dots_still_work()
    {
        var (client, handler) = TestSettings.Create();
        handler.Respond("DELETE", "/api/queues/%2Forders/.../contents", "", HttpStatusCode.NoContent);

        await client.PurgeQueueAsync(TestSettings.Orders, "/orders", "...");

        handler.Requests.Should().ContainSingle();
    }

    private const string LeakyReason = "Validation failed\n\namqp://user:p@ss@remote/vhost is not a valid shovel URI (see https://admin:hunter2@proxy.local/x)";

    [Fact]
    public async Task Userinfo_in_a_broker_error_reason_is_redacted_everywhere_it_travels()
    {
        var (client, handler) = TestSettings.Create();
        handler.Respond("PUT", "/api/parameters/shovel/%2Forders/drain",
            $$"""{"error":"bad_request","reason":{{System.Text.Json.JsonSerializer.Serialize(LeakyReason)}}}""", HttpStatusCode.BadRequest);

        var act = () => client.CreateShovelAsync(TestSettings.Orders, "/orders",
            new CreateShovelRequest("drain", "payments-dlq", null, "order-events", null, SourceUri: "amqp://user:p@ss@remote"));

        var ex = (await act.Should().ThrowAsync<ManagementApiException>()).Which;
        foreach (var text in new[] { ex.Reason!, ex.Message, FriendlyRabbitError.From(ex) })
        {
            text.Should().NotContain("p@ss").And.NotContain("user:").And.NotContain("hunter2");
        }

        ex.Reason.Should().Contain("amqp://***@remote/vhost").And.Contain("https://***@proxy.local/x");
    }

    [Theory]
    [InlineData("amqp://user:p@ss@remote", "amqp://***@remote")]
    [InlineData("amqps://u:x@h:5671/v", "amqps://***@h:5671/v")]
    [InlineData("see 'http://a:b@c' and \"https://d@e\"", "see 'http://***@c' and \"https://***@e\"")]
    [InlineData("no uri here, just user@example.com", "no uri here, just user@example.com")]
    public void Redact_strips_uri_userinfo(string input, string expected) =>
        UriRedaction.Redact(input).Should().Be(expected);

    [Fact]
    public void ManagementApiException_redacts_even_when_constructed_directly()
    {
        var ex = new ManagementApiException(400, "PUT", "/api/x", "amqp://user:secret@remote");

        ex.Reason.Should().Be("amqp://***@remote");
        ex.Message.Should().NotContain("secret");
    }

    [Fact]
    public void FriendlyRabbitError_redacts_userinfo_from_arbitrary_exception_text() =>
        FriendlyRabbitError.From(new InvalidOperationException("could not reach amqp://guest:guest@remote"))
            .Should().NotContain("guest:guest");
}
