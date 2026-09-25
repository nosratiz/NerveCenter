using FluentAssertions;
using SbConsole.Plugins.RabbitMq.Routing;

namespace SbConsole.Plugins.RabbitMq.Tests.Routing;

public class NameSuggesterTests
{
    private static readonly string[] SeededQueues =
    [
        "order-events.q", "billing.retry", "payments-dlq", "audit.sink", "shipment.updates.q",
        "invoice.issued.q", "notify.email.q", "notify.sms.q", "legacy.import.q", "billing.payments.q",
    ];

    [Theory]
    [InlineData("shipping.", "shipment.updates.q")] // the 1d mockup
    [InlineData("paymnts-dlq", "payments-dlq")]
    [InlineData("notify.push", "notify.sms.q")] // same prefix; "sms.q" is nearer "push" than "email.q"
    [InlineData("Audit.Sink", "audit.sink")]
    [InlineData("invoice", "invoice.issued.q")]
    public void Suggests_the_closest_name(string input, string expected) =>
        NameSuggester.Closest(input, SeededQueues).Should().Be(expected);

    [Theory]
    [InlineData("zzqxvw")]
    [InlineData("kafka-topic-thing")]
    [InlineData("")]
    [InlineData("   ")]
    public void Gibberish_gets_no_suggestion(string input) =>
        NameSuggester.Closest(input, SeededQueues).Should().BeNull();

    [Fact]
    public void No_candidates_is_null() => NameSuggester.Closest("orders", []).Should().BeNull();

    [Theory]
    [InlineData("", "", 0)]
    [InlineData("abc", "", 3)]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("flaw", "lawn", 2)]
    [InlineData("same", "same", 0)]
    public void Levenshtein_distance(string a, string b, int expected) =>
        Levenshtein.Distance(a, b).Should().Be(expected);
}
