using System.Text;
using SbConsole.Plugins.RabbitMq.Client;

namespace SbConsole.Plugins.RabbitMq.Tests.Pages;

/// <summary>
/// The mockup's payments-dlq: messages dead-lettered out of billing.payments.q (published through
/// billing.direct) and delivered here via billing.retry.dlx.
/// </summary>
internal static class PaymentsDlq
{
    public const string Queue = "payments-dlq";
    public const string SourceQueue = "billing.payments.q";
    public const string SourceExchange = "billing.direct";
    public const string Dlx = "billing.retry.dlx";

    public static readonly DateTimeOffset At = new(2026, 9, 26, 2, 11, 4, TimeSpan.Zero);

    public const string Json = """{"paymentId":"pay_8814c2","orderId":"ord_41908","amount":{"value":149.00,"ccy":"GBP"}}""";

    public static RabbitMessage Message(int index, string? id, string routingKey, string reason = "expired", long count = 1,
        string body = Json, bool persistent = true, IReadOnlyDictionary<string, string>? headers = null) =>
        new(index, id, "ord_41908", Dlx, routingKey, false, "application/json", null, persistent ? 2 : 1, 0, "billing-svc",
            At.AddSeconds(-index * 6),
            headers ?? new Dictionary<string, string> { ["x-first-death-queue"] = SourceQueue, ["x-first-death-exchange"] = SourceExchange, ["tenant"] = "uk" },
            Encoding.UTF8.GetBytes(body),
            [new DeathRecord(SourceQueue, SourceExchange, reason, [routingKey], count, At.AddSeconds(-index * 6))],
            SourceQueue, SourceExchange, null);

    public static List<RabbitMessage> Messages() =>
    [
        Message(0, "pay_8814c2", "payment.capture.failed", "rejected", 5),
        Message(1, "pay_8814bf", "payment.capture.failed", "rejected", 5),
        Message(2, "pay_8813a0", "payment.capture.request"),
        Message(3, null, "audit.write", "maxlen", body: "plain text"),
    ];

    public static List<ExchangeSummary> Exchanges() =>
    [
        .. SeededTopology.Exchanges(),
        SeededTopology.Exchange(SourceExchange, "direct"),
    ];
}
