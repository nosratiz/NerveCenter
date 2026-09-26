using System.Collections;
using System.Globalization;
using System.Text;
using RabbitMQ.Client;

namespace SbConsole.Plugins.RabbitMq.Client;

/// <summary>
/// Pure mapping between RabbitMQ.Client 7.x types and the plugin's records. AMQP carries header
/// strings as byte[] (longstr), so every header value is decoded to a readable string here;
/// x-death -- a list of tables whose strings are byte[], "count" a long and "time" an
/// AmqpTimestamp -- is parsed into <see cref="DeathRecord"/>s and left out of Headers. Parsing is
/// lenient: a missing or oddly typed field degrades to ""/0/null, never an exception, because a
/// message's headers are written by whatever published it.
/// </summary>
internal static class AmqpMessageMapper
{
    private const string XDeath = "x-death";

    public static RabbitMessage Map(
        int index,
        string exchange,
        string routingKey,
        bool redelivered,
        IReadOnlyBasicProperties properties,
        ReadOnlyMemory<byte> body)
    {
        var rawHeaders = properties.IsHeadersPresent() ? properties.Headers : null;
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        IReadOnlyList<DeathRecord> deaths = [];
        string? firstDeathQueue = null;
        string? firstDeathExchange = null;
        long? deliveryCount = null;

        if (rawHeaders is not null)
        {
            foreach (var (key, value) in rawHeaders)
            {
                if (key == XDeath)
                {
                    deaths = ParseDeaths(value);
                    continue;
                }

                headers[key] = Decode(value);
                switch (key)
                {
                    case "x-first-death-queue":
                        firstDeathQueue = headers[key];
                        break;
                    case "x-first-death-exchange":
                        firstDeathExchange = headers[key];
                        break;
                    case "x-delivery-count":
                        deliveryCount = AsLong(value);
                        break;
                }
            }
        }

        return new RabbitMessage(
            Index: index,
            MessageId: properties.IsMessageIdPresent() ? properties.MessageId : null,
            CorrelationId: properties.IsCorrelationIdPresent() ? properties.CorrelationId : null,
            Exchange: exchange,
            RoutingKey: routingKey,
            Redelivered: redelivered,
            ContentType: properties.IsContentTypePresent() ? properties.ContentType : null,
            ContentEncoding: properties.IsContentEncodingPresent() ? properties.ContentEncoding : null,
            DeliveryMode: properties.IsDeliveryModePresent() && properties.DeliveryMode == DeliveryModes.Persistent ? 2 : 1,
            Priority: properties.IsPriorityPresent() ? properties.Priority : null,
            AppId: properties.IsAppIdPresent() ? properties.AppId : null,
            Timestamp: properties.IsTimestampPresent() ? ToDateTimeOffset(properties.Timestamp) : null,
            Headers: headers,
            Body: body.ToArray(), // always a copy: the client may reuse the delivery buffer
            Deaths: deaths,
            FirstDeathQueue: firstDeathQueue,
            FirstDeathExchange: firstDeathExchange,
            DeliveryCount: deliveryCount);
    }

    public static BasicProperties ToProperties(PublishRequest request)
    {
        var properties = new BasicProperties
        {
            DeliveryMode = request.Persistent ? DeliveryModes.Persistent : DeliveryModes.Transient,
        };

        if (request.Priority is { } priority)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(priority, byte.MinValue, nameof(request.Priority));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(priority, byte.MaxValue, nameof(request.Priority));
            properties.Priority = (byte)priority;
        }

        if (request.ContentType is not null)
        {
            properties.ContentType = request.ContentType;
        }

        if (request.MessageId is not null)
        {
            properties.MessageId = request.MessageId;
        }

        if (request.CorrelationId is not null)
        {
            properties.CorrelationId = request.CorrelationId;
        }

        if (request.Timestamp is { } timestamp)
        {
            properties.Timestamp = new AmqpTimestamp(timestamp.ToUnixTimeSeconds());
        }

        if (request.AppId is not null)
        {
            properties.AppId = request.AppId;
        }

        if (request.Headers is { Count: > 0 } requestHeaders)
        {
            properties.Headers = requestHeaders.ToDictionary(h => h.Key, h => (object?)h.Value, StringComparer.Ordinal);
        }

        return properties;
    }

    // ---------------------------------------------------------------- x-death

    private static List<DeathRecord> ParseDeaths(object? value)
    {
        var deaths = new List<DeathRecord>();
        if (value is not IList entries)
        {
            return deaths;
        }

        foreach (var entry in entries)
        {
            if (entry is not IDictionary<string, object?> table)
            {
                continue;
            }

            deaths.Add(new DeathRecord(
                Queue: AsString(Field(table, "queue")) ?? "",
                Exchange: AsString(Field(table, "exchange")) ?? "",
                Reason: AsString(Field(table, "reason")) ?? "",
                RoutingKeys: AsStringList(Field(table, "routing-keys")),
                Count: AsLong(Field(table, "count")) ?? 0,
                Time: Field(table, "time") is AmqpTimestamp time ? ToDateTimeOffset(time) : null));
        }

        return deaths;
    }

    private static object? Field(IDictionary<string, object?> table, string key) =>
        table.TryGetValue(key, out var value) ? value : null;

    private static List<string> AsStringList(object? value) => value switch
    {
        IList list and not byte[] => list.Cast<object?>().Select(AsString).OfType<string>().ToList(),
        _ when AsString(value) is { } single => [single],
        _ => [],
    };

    private static string? AsString(object? value) => value switch
    {
        byte[] bytes => Encoding.UTF8.GetString(bytes),
        string s => s,
        _ => null,
    };

    private static long? AsLong(object? value) => value switch
    {
        long l => l,
        int i => i,
        short s => s,
        byte b => b,
        sbyte sb => sb,
        ushort us => us,
        uint ui => ui,
        ulong ul when ul <= long.MaxValue => (long)ul,
        _ => null,
    };

    // ---------------------------------------------------------------- header decoding

    private static string Decode(object? value) => value switch
    {
        null => "",
        byte[] bytes => Encoding.UTF8.GetString(bytes),
        string s => s,
        bool b => b ? "true" : "false",
        AmqpTimestamp ts => ToDateTimeOffset(ts)?.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
            ?? ts.UnixTime.ToString(CultureInfo.InvariantCulture),
        IDictionary<string, object?> table =>
            "{" + string.Join(", ", table.Select(kv => $"{kv.Key}: {Decode(kv.Value)}")) + "}",
        IList list => "[" + string.Join(", ", list.Cast<object?>().Select(Decode)) + "]",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    // Null for a value outside DateTimeOffset's range (a publisher that wrote milliseconds, say)
    // rather than letting FromUnixTimeSeconds throw.
    private static DateTimeOffset? ToDateTimeOffset(AmqpTimestamp timestamp) =>
        timestamp.UnixTime is >= MinUnixSeconds and <= MaxUnixSeconds
            ? DateTimeOffset.FromUnixTimeSeconds(timestamp.UnixTime)
            : null;

    private const long MinUnixSeconds = -62_135_596_800;
    private const long MaxUnixSeconds = 253_402_300_799;
}
