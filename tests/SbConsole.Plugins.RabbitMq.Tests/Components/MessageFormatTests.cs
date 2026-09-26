using System.Text;
using FluentAssertions;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Plugins.RabbitMq.Components;

namespace SbConsole.Plugins.RabbitMq.Tests.Components;

public class MessageFormatTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 26, 2, 11, 4, TimeSpan.Zero);

    internal static RabbitMessage Message(
        string? id = "pay_8814c2",
        string body = "{}",
        IReadOnlyList<DeathRecord>? deaths = null,
        string? firstDeathExchange = null,
        string? firstDeathQueue = null,
        string exchange = "billing.retry.dlx",
        string routingKey = "payment.capture.failed",
        int deliveryMode = 2,
        IReadOnlyDictionary<string, string>? headers = null,
        byte[]? bytes = null,
        int index = 0) =>
        new(index, id, "ord_41908", exchange, routingKey, false, "application/json", null, deliveryMode, 3, "billing-svc", At,
            headers ?? new Dictionary<string, string>(), bytes ?? Encoding.UTF8.GetBytes(body), deaths ?? [], firstDeathQueue, firstDeathExchange, null);

    private static DeathRecord Death(string reason, long count, string exchange = "billing.direct", string queue = "billing.payments.q", params string[] keys) =>
        new(queue, exchange, reason, keys, count, At);

    [Fact]
    public void Death_summary_is_the_top_death_reason_with_its_count_when_more_than_one()
    {
        MessageFormat.DeathSummary(Message(deaths: [Death("rejected", 5), Death("expired", 1)])).Should().Be("rejected ×5");
        MessageFormat.DeathSummary(Message(deaths: [Death("expired", 1)])).Should().Be("expired");
        MessageFormat.DeathSummary(Message(deaths: [Death("maxlen", 1)])).Should().Be("maxlen");
        MessageFormat.DeathSummary(Message()).Should().BeNull();
    }

    [Fact]
    public void Death_count_sums_every_death_record()
    {
        MessageFormat.DeathCount(Message(deaths: [Death("rejected", 5), Death("expired", 1)])).Should().Be(6);
        MessageFormat.DeathCount(Message()).Should().Be(0);
    }

    [Fact]
    public void Original_routing_key_comes_from_the_first_death_else_the_message()
    {
        var deaths = new[]
        {
            Death("rejected", 5, "billing.direct", "billing.payments.q", "payment.capture"),
            Death("expired", 1, "order-events", "order-events.q", "order.uk.created", "order.uk.other"),
        };

        var dead = Message(deaths: deaths, firstDeathExchange: "order-events", firstDeathQueue: "order-events.q");
        MessageFormat.FirstDeath(dead)!.Exchange.Should().Be("order-events");
        MessageFormat.OriginalRoutingKey(dead).Should().Be("order.uk.created");

        MessageFormat.OriginalRoutingKey(Message(routingKey: "k")).Should().Be("k");
        MessageFormat.OriginalRoutingKey(Message(deaths: [Death("expired", 1)], routingKey: "fallback")).Should().Be("fallback");
        // No x-first-death headers: the oldest death record (last in x-death) is the first death.
        MessageFormat.FirstDeath(Message(deaths: deaths))!.Queue.Should().Be("order-events.q");
    }

    [Fact]
    public void Json_bodies_are_pretty_printed_keeping_number_text()
    {
        var view = MessageFormat.RenderBody(Encoding.UTF8.GetBytes("""{"paymentId":"pay_8814c2","amount":{"value":149.00,"ccy":"GBP"}}"""));

        view.Kind.Should().Be(BodyKind.Json);
        view.Text.Should().Be("{\n  \"paymentId\": \"pay_8814c2\",\n  \"amount\": {\n    \"value\": 149.00,\n    \"ccy\": \"GBP\"\n  }\n}");
    }

    [Fact]
    public void Non_json_utf8_is_shown_as_text()
    {
        MessageFormat.RenderBody(Encoding.UTF8.GetBytes("hello — wörld")).Should().Be(new BodyView(BodyKind.Text, "hello — wörld", false));
        MessageFormat.RenderBody(Encoding.UTF8.GetBytes("{not json")).Kind.Should().Be(BodyKind.Text);
        MessageFormat.RenderBody([]).Kind.Should().Be(BodyKind.Text);
    }

    [Fact]
    public void Binary_bodies_get_a_hex_preview_of_the_first_256_bytes()
    {
        var view = MessageFormat.RenderBody([0xff, 0xfe, 0x00, 0x41]);
        view.Kind.Should().Be(BodyKind.Binary);
        view.Text.Should().Be("0000  ff fe 00 41");
        view.Truncated.Should().BeFalse();

        var big = MessageFormat.RenderBody(Enumerable.Range(0, 300).Select(i => (byte)(i % 7 == 0 ? 0xff : i)).ToArray());
        big.Kind.Should().Be(BodyKind.Binary);
        big.Truncated.Should().BeTrue();
        big.Text.Split('\n').Should().HaveCount(16);
        big.Text.Split('\n')[1].Should().StartWith("0010  10 11 12 13");
    }

    [Fact]
    public void Utf8_with_control_characters_counts_as_binary()
    {
        MessageFormat.RenderBody([0x41, 0x00, 0x42]).Kind.Should().Be(BodyKind.Binary);
        MessageFormat.RenderBody(Encoding.UTF8.GetBytes("a\tb\r\nc")).Kind.Should().Be(BodyKind.Text);
    }

    [Fact]
    public void Delivery_mode_reads_as_a_number_and_a_word()
    {
        MessageFormat.DeliveryMode(Message(deliveryMode: 2)).Should().Be("2 · persistent");
        MessageFormat.DeliveryMode(Message(deliveryMode: 1)).Should().Be("1 · transient");
    }

    [Fact]
    public void To_publish_request_carries_ids_headers_and_persistence()
    {
        var headers = new Dictionary<string, string> { ["tenant"] = "uk" };
        var message = Message(headers: headers, deliveryMode: 1, body: "payload");

        var request = MessageFormat.ToPublishRequest(message, "", "payments-dlq");

        request.Exchange.Should().Be("");
        request.RoutingKey.Should().Be("payments-dlq");
        request.Body.Should().Equal(Encoding.UTF8.GetBytes("payload"));
        request.Persistent.Should().BeFalse();
        request.Priority.Should().Be(3);
        request.ContentType.Should().Be("application/json");
        request.Headers.Should().BeEquivalentTo(headers);
        request.MessageId.Should().Be("pay_8814c2");
        request.CorrelationId.Should().Be("ord_41908");
        request.Timestamp.Should().Be(At);
        request.AppId.Should().Be("billing-svc");

        MessageFormat.ToPublishRequest(Message(deliveryMode: 2), "x", "k").Persistent.Should().BeTrue();
    }
}
