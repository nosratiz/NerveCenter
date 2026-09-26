using System.Net;
using System.Text.Json;
using FluentAssertions;
using SbConsole.Plugins.RabbitMq.Client;

namespace SbConsole.Plugins.RabbitMq.Tests.Client;

public class ManagementApiClientWriteTests
{
    [Fact]
    public async Task CreateExchange_puts_type_flags_and_alternate_exchange()
    {
        var (client, handler) = Created("PUT", "/api/exchanges/%2Forders/orders.in");

        await client.CreateExchangeAsync(TestSettings.Orders, "/orders",
            new CreateExchangeRequest("orders.in", "topic", Durable: true, AutoDelete: false, Internal: true, AlternateExchange: "orders.unrouted"));

        var request = handler.Requests.Single();
        request.Method.Should().Be(HttpMethod.Put);
        request.ContentType.Should().Be("application/json");
        Json(request.Body).Should().Be(Json("""
            {"type":"topic","durable":true,"auto_delete":false,"internal":true,"arguments":{"alternate-exchange":"orders.unrouted"}}
            """));
    }

    [Fact]
    public async Task CreateExchange_without_alternate_sends_empty_arguments()
    {
        var (client, handler) = Created("PUT", "/api/exchanges/%2Forders/plain");

        await client.CreateExchangeAsync(TestSettings.Orders, "/orders", new CreateExchangeRequest("plain", "direct"));

        Json(handler.Requests.Single().Body).Should().Be(Json("""
            {"type":"direct","durable":true,"auto_delete":false,"internal":false,"arguments":{}}
            """));
    }

    [Fact]
    public async Task DeleteExchange_maps_the_default_exchange_to_amq_default()
    {
        var (client, handler) = TestSettings.Create();
        handler.Respond("DELETE", "/api/exchanges/%2Forders/amq.default", "", HttpStatusCode.NoContent)
            .Respond("DELETE", "/api/exchanges/%2Forders/old%20ex", "", HttpStatusCode.NoContent);

        await client.DeleteExchangeAsync(TestSettings.Orders, "/orders", "");
        await client.DeleteExchangeAsync(TestSettings.Orders, "/orders", "old ex");

        handler.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Delete && r.Body == null);
    }

    [Fact]
    public async Task CreateQueue_puts_x_arguments_and_omits_nulls()
    {
        var (client, handler) = Created("PUT", "/api/queues/%2Forders/work.q");

        await client.CreateQueueAsync(TestSettings.Orders, "/orders", new CreateQueueRequest(
            "work.q", "quorum", DeadLetterExchange: "work.dlx", DeadLetterRoutingKey: "dead",
            MessageTtlMs: 60000, MaxLength: 1000, Overflow: "reject-publish"));

        Json(handler.Requests.Single().Body).Should().Be(Json("""
            {"durable":true,"auto_delete":false,"arguments":{
              "x-queue-type":"quorum","x-dead-letter-exchange":"work.dlx","x-dead-letter-routing-key":"dead",
              "x-message-ttl":60000,"x-max-length":1000,"x-overflow":"reject-publish"}}
            """));
    }

    [Fact]
    public async Task CreateQueue_minimal_sends_only_the_queue_type()
    {
        var (client, handler) = Created("PUT", "/api/queues/%2Forders/simple");

        await client.CreateQueueAsync(TestSettings.Orders, "/orders", new CreateQueueRequest("simple"));

        Json(handler.Requests.Single().Body).Should().Be(Json("""
            {"durable":true,"auto_delete":false,"arguments":{"x-queue-type":"classic"}}
            """));
    }

    [Fact]
    public async Task DeleteQueue_and_purge_use_delete_on_the_queue_and_its_contents()
    {
        var (client, handler) = TestSettings.Create();
        handler.Respond("DELETE", "/api/queues/%2Forders/q1", "", HttpStatusCode.NoContent)
            .Respond("DELETE", "/api/queues/%2Forders/q1/contents", "", HttpStatusCode.NoContent);

        await client.DeleteQueueAsync(TestSettings.Orders, "/orders", "q1");
        await client.PurgeQueueAsync(TestSettings.Orders, "/orders", "q1");

        handler.Requests.Select(r => $"{r.Method} {r.Uri.AbsolutePath}").Should().Equal(
            "DELETE /api/queues/%2Forders/q1",
            "DELETE /api/queues/%2Forders/q1/contents");
    }

    [Fact]
    public async Task AddBinding_posts_routing_key_and_arguments()
    {
        var (client, handler) = Created("POST", "/api/bindings/%2Forders/e/order-events/q/audit.sink");

        await client.AddBindingAsync(TestSettings.Orders, "/orders", "order-events", "audit.sink", "order.*.created",
            new Dictionary<string, object?> { ["x-match"] = "all", ["n"] = 3L });

        var request = handler.Requests.Single();
        request.Method.Should().Be(HttpMethod.Post);
        Json(request.Body).Should().Be(Json("""
            {"routing_key":"order.*.created","arguments":{"x-match":"all","n":3}}
            """));
    }

    [Fact]
    public async Task AddBinding_from_the_default_exchange_uses_amq_default()
    {
        var (client, handler) = Created("POST", "/api/bindings/%2Forders/e/amq.default/q/q1");

        await client.AddBindingAsync(TestSettings.Orders, "/orders", "", "q1", "q1", new Dictionary<string, object?>());

        handler.Requests.Single().Uri.AbsolutePath.Should().Be("/api/bindings/%2Forders/e/amq.default/q/q1");
    }

    [Fact]
    public async Task RemoveBinding_escapes_the_already_encoded_properties_key()
    {
        var (client, handler) = TestSettings.Create();
        handler.Respond("DELETE", "/api/bindings/%2Forders/e/order-events/q/order-events.q/order.%252A.amended", "", HttpStatusCode.NoContent);

        await client.RemoveBindingAsync(TestSettings.Orders, "/orders", "order-events", "order-events.q", "order.%2A.amended");

        handler.Requests.Single().Method.Should().Be(HttpMethod.Delete);
    }

    [Fact]
    public async Task CreateShovel_to_an_exchange_builds_local_uris_from_the_vhost()
    {
        var (client, handler) = Created("PUT", "/api/parameters/shovel/%2Forders/move-it");

        await client.CreateShovelAsync(TestSettings.Orders, "/orders", new CreateShovelRequest(
            "move-it", SourceQueue: "legacy.import.q", DestinationQueue: null, DestinationExchange: "order-events",
            DestinationRoutingKey: "order.legacy.created"));

        Json(handler.Requests.Single().Body).Should().Be(Json("""
            {"value":{"src-uri":"amqp:///%2Forders","src-queue":"legacy.import.q",
                      "dest-uri":"amqp:///%2Forders","dest-exchange":"order-events","dest-exchange-key":"order.legacy.created",
                      "ack-mode":"on-confirm","src-delete-after":"never"}}
            """));
    }

    [Fact]
    public async Task CreateShovel_to_a_queue_uses_given_uris_and_never_sends_a_routing_key()
    {
        var (client, handler) = Created("PUT", "/api/parameters/shovel/%2Forders/to-dr");

        await client.CreateShovelAsync(TestSettings.Orders, "/orders", new CreateShovelRequest(
            "to-dr", "billing.retry", DestinationQueue: "billing.dr", DestinationExchange: null,
            DestinationRoutingKey: "ignored", AckMode: "on-publish",
            SourceUri: "amqp://", DestinationUri: "amqps://dr.example/%2Forders", SourceDeleteAfter: "queue-length"));

        Json(handler.Requests.Single().Body).Should().Be(Json("""
            {"value":{"src-uri":"amqp://","src-queue":"billing.retry",
                      "dest-uri":"amqps://dr.example/%2Forders","dest-queue":"billing.dr",
                      "ack-mode":"on-publish","src-delete-after":"queue-length"}}
            """));
    }

    [Fact]
    public async Task CreateShovel_requires_exactly_one_destination()
    {
        var (client, _) = TestSettings.Create();

        var act = () => client.CreateShovelAsync(TestSettings.Orders, "/orders",
            new CreateShovelRequest("bad", "src", null, null, null));

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task DeleteShovel_and_restart_hit_parameter_and_restart_endpoints()
    {
        var (client, handler) = TestSettings.Create();
        handler.Respond("DELETE", "/api/parameters/shovel/%2Forders/legacy-migrate", "", HttpStatusCode.NoContent)
            .Respond("DELETE", "/api/shovels/vhost/%2Forders/legacy-migrate/restart", "", HttpStatusCode.NoContent);

        await client.DeleteShovelAsync(TestSettings.Orders, "/orders", "legacy-migrate");
        await client.RestartShovelAsync(TestSettings.Orders, "/orders", "legacy-migrate");

        handler.Requests.Select(r => $"{r.Method} {r.Uri.AbsolutePath}").Should().Equal(
            "DELETE /api/parameters/shovel/%2Forders/legacy-migrate",
            "DELETE /api/shovels/vhost/%2Forders/legacy-migrate/restart");
    }

    private static (ManagementApiClient Client, FakeManagementHandler Handler) Created(string method, string path)
    {
        var (client, handler) = TestSettings.Create();
        handler.Respond(method, path, "", HttpStatusCode.Created);
        return (client, handler);
    }

    // Normalised JSON so assertions compare structure and values, not whitespace.
    private static string Json(string? json) =>
        JsonSerializer.Serialize(JsonDocument.Parse(json ?? "null").RootElement);
}
