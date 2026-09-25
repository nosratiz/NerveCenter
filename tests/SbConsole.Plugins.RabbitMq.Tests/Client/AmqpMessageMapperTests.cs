using System.Text;
using FluentAssertions;
using RabbitMQ.Client;
using SbConsole.Plugins.RabbitMq.Client;

namespace SbConsole.Plugins.RabbitMq.Tests.Client;

public class AmqpMessageMapperTests
{
    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    // 2026-09-25T10:00:00Z
    private const long Unix = 1790330400;

    // The shape RabbitMQ.Client 7.x hands back for x-death: a List<object> of dictionaries whose
    // strings are byte[], "routing-keys" a List<object> of byte[], "count" a long and "time" an
    // AmqpTimestamp.
    private static Dictionary<string, object?> Death(string queue, string reason, long count, params string[] keys) => new()
    {
        ["count"] = count,
        ["exchange"] = B("orders.dlx"),
        ["queue"] = B(queue),
        ["reason"] = B(reason),
        ["routing-keys"] = keys.Select(k => (object)B(k)).ToList(),
        ["time"] = new AmqpTimestamp(Unix),
    };

    private static RabbitMessage Map(BasicProperties props, string body = "{}", int index = 0) =>
        AmqpMessageMapper.Map(index, "orders", "order.uk.created", redelivered: true, props, B(body));

    [Fact]
    public void Maps_envelope_and_basic_properties()
    {
        var props = new BasicProperties
        {
            MessageId = "m-1",
            CorrelationId = "c-1",
            ContentType = "application/json",
            ContentEncoding = "utf-8",
            DeliveryMode = DeliveryModes.Persistent,
            Priority = 5,
            AppId = "orders-api",
            Timestamp = new AmqpTimestamp(Unix),
        };

        var message = Map(props, "{\"id\":1}", index: 3);

        message.Index.Should().Be(3);
        message.Exchange.Should().Be("orders");
        message.RoutingKey.Should().Be("order.uk.created");
        message.Redelivered.Should().BeTrue();
        message.MessageId.Should().Be("m-1");
        message.CorrelationId.Should().Be("c-1");
        message.ContentType.Should().Be("application/json");
        message.ContentEncoding.Should().Be("utf-8");
        message.DeliveryMode.Should().Be(2);
        message.IsPersistent.Should().BeTrue();
        message.Priority.Should().Be(5);
        message.AppId.Should().Be("orders-api");
        message.Timestamp.Should().Be(new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero));
        Encoding.UTF8.GetString(message.Body).Should().Be("{\"id\":1}");
        message.Headers.Should().BeEmpty();
        message.Deaths.Should().BeEmpty();
        message.FirstDeathQueue.Should().BeNull();
        message.DeliveryCount.Should().BeNull();
    }

    [Fact]
    public void Absent_properties_map_to_null_and_transient()
    {
        var message = Map(new BasicProperties());

        message.MessageId.Should().BeNull();
        message.ContentType.Should().BeNull();
        message.Priority.Should().BeNull();
        message.Timestamp.Should().BeNull();
        message.AppId.Should().BeNull();
        message.DeliveryMode.Should().Be(1);
    }

    [Fact]
    public void Body_is_copied_not_aliased()
    {
        var buffer = B("abc");
        var message = AmqpMessageMapper.Map(0, "", "q", false, new BasicProperties(), buffer);

        buffer[0] = (byte)'z';

        Encoding.UTF8.GetString(message.Body).Should().Be("abc");
    }

    [Fact]
    public void Decodes_header_values_to_readable_strings()
    {
        var props = new BasicProperties
        {
            Headers = new Dictionary<string, object?>
            {
                ["tenant"] = B("acme-uk"),
                ["plain"] = "already-a-string",
                ["attempt"] = 3,
                ["big"] = 9_000_000_000L,
                ["ratio"] = 1.5d,
                ["flag"] = true,
                ["sent"] = new AmqpTimestamp(Unix),
                ["list"] = new List<object> { B("a"), 2L },
                ["dict"] = new Dictionary<string, object?> { ["k"] = B("v"), ["n"] = 1 },
                ["nothing"] = null,
            },
        };

        var headers = Map(props).Headers;

        headers.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["tenant"] = "acme-uk",
            ["plain"] = "already-a-string",
            ["attempt"] = "3",
            ["big"] = "9000000000",
            ["ratio"] = "1.5",
            ["flag"] = "true",
            ["sent"] = "2026-09-25T10:00:00Z",
            ["list"] = "[a, 2]",
            ["dict"] = "{k: v, n: 1}",
            ["nothing"] = "",
        });
    }

    [Fact]
    public void Parses_x_death_into_death_records_and_keeps_it_out_of_headers()
    {
        var props = new BasicProperties
        {
            Headers = new Dictionary<string, object?>
            {
                ["x-death"] = new List<object>
                {
                    Death("payments", "expired", 2, "payment.captured"),
                    Death("payments-retry", "rejected", 1, "payment.captured", "payment.retry"),
                },
                ["x-first-death-queue"] = B("payments"),
                ["x-first-death-exchange"] = B("payments"),
                ["x-first-death-reason"] = B("expired"),
            },
        };

        var message = Map(props);

        // Structural comparison: records compare their RoutingKeys lists by reference.
        message.Deaths.Should().BeEquivalentTo(
            [
                new DeathRecord("payments", "orders.dlx", "expired", ["payment.captured"], 2,
                    new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero)),
                new DeathRecord("payments-retry", "orders.dlx", "rejected", ["payment.captured", "payment.retry"], 1,
                    new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero)),
            ],
            o => o.WithStrictOrdering().ComparingByMembers<DeathRecord>());
        message.FirstDeathQueue.Should().Be("payments");
        message.FirstDeathExchange.Should().Be("payments");
        message.Headers.Should().NotContainKey("x-death");
        message.Headers.Should().ContainKey("x-first-death-reason").WhoseValue.Should().Be("expired");
    }

    [Fact]
    public void Tolerates_missing_and_odd_x_death_fields()
    {
        var props = new BasicProperties
        {
            Headers = new Dictionary<string, object?>
            {
                ["x-death"] = new List<object>
                {
                    new Dictionary<string, object?> { ["queue"] = "q-as-string", ["count"] = 4 },
                    "not a table",
                    new Dictionary<string, object?> { ["routing-keys"] = B("single-key"), ["time"] = "garbage", ["count"] = B("x") },
                },
            },
        };

        var deaths = Map(props).Deaths;

        deaths.Should().HaveCount(2);
        deaths[0].Queue.Should().Be("q-as-string");
        deaths[0].Exchange.Should().Be("");
        deaths[0].Reason.Should().Be("");
        deaths[0].RoutingKeys.Should().BeEmpty();
        deaths[0].Count.Should().Be(4);
        deaths[0].Time.Should().BeNull();
        deaths[1].Queue.Should().Be("");
        deaths[1].RoutingKeys.Should().Equal("single-key");
        deaths[1].Count.Should().Be(0);
        deaths[1].Time.Should().BeNull();
    }

    [Fact]
    public void X_death_that_is_not_a_list_yields_no_deaths()
    {
        var props = new BasicProperties { Headers = new Dictionary<string, object?> { ["x-death"] = B("nope") } };

        var message = Map(props);

        message.Deaths.Should().BeEmpty();
        message.Headers.Should().NotContainKey("x-death");
    }

    [Theory]
    [InlineData(3)]
    [InlineData(3L)]
    public void Reads_the_quorum_delivery_count(object raw)
    {
        var props = new BasicProperties { Headers = new Dictionary<string, object?> { ["x-delivery-count"] = raw } };

        Map(props).DeliveryCount.Should().Be(3);
    }

    [Fact]
    public void ToProperties_maps_a_full_publish_request()
    {
        var request = new PublishRequest(
            "orders", "order.uk.created", B("{}"),
            Persistent: true,
            Priority: 4,
            ContentType: "application/json",
            Headers: new Dictionary<string, string> { ["tenant"] = "acme" },
            MessageId: "m-9",
            CorrelationId: "c-9",
            Timestamp: new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero),
            AppId: "sbconsole");

        var props = AmqpMessageMapper.ToProperties(request);

        props.DeliveryMode.Should().Be(DeliveryModes.Persistent);
        props.Priority.Should().Be(4);
        props.ContentType.Should().Be("application/json");
        props.MessageId.Should().Be("m-9");
        props.CorrelationId.Should().Be("c-9");
        props.Timestamp.UnixTime.Should().Be(Unix);
        props.AppId.Should().Be("sbconsole");
        props.Headers.Should().ContainKey("tenant").WhoseValue.Should().Be("acme");
    }

    [Fact]
    public void ToProperties_leaves_optional_fields_absent()
    {
        var props = AmqpMessageMapper.ToProperties(new PublishRequest("", "q", B("x"), Persistent: false));

        props.DeliveryMode.Should().Be(DeliveryModes.Transient);
        props.IsPriorityPresent().Should().BeFalse();
        props.IsContentTypePresent().Should().BeFalse();
        props.IsMessageIdPresent().Should().BeFalse();
        props.IsCorrelationIdPresent().Should().BeFalse();
        props.IsTimestampPresent().Should().BeFalse();
        props.IsAppIdPresent().Should().BeFalse();
        props.IsHeadersPresent().Should().BeFalse();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(256)]
    public void ToProperties_rejects_a_priority_outside_a_byte(int priority)
    {
        var act = () => AmqpMessageMapper.ToProperties(new PublishRequest("", "q", B("x"), Priority: priority));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
