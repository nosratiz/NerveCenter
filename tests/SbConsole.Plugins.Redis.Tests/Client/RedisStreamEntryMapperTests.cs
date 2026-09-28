using FluentAssertions;
using SbConsole.Plugins.Redis.Client;

namespace SbConsole.Plugins.Redis.Tests.Client;

// Both StackExchange.Redis.StreamEntry and our own Client.StreamEntry are in scope here (via
// `using`, not the enclosing namespace, since this test file's own namespace is
// SbConsole.Plugins.Redis.Tests.Client, not .Client itself) -- so, unlike inside the plugin's own
// Client/ files, both names need full qualification here to avoid CS0104 ambiguity.
public class RedisStreamEntryMapperTests
{
    [Fact]
    public void Maps_id_and_field_value_pairs()
    {
        var entry = new StackExchange.Redis.StreamEntry(
            "1696000000123-0",
            [new StackExchange.Redis.NameValueEntry("event", "order.created"), new StackExchange.Redis.NameValueEntry("orderId", "42")]);

        var mapped = RedisStreamEntryMapper.ToStreamEntry(entry);

        mapped.Id.Should().Be("1696000000123-0");
        mapped.Fields.Should().Equal(
            new StreamFieldValue("event", "order.created"),
            new StreamFieldValue("orderId", "42"));
    }

    [Fact]
    public void Maps_an_entry_with_no_fields()
    {
        var entry = new StackExchange.Redis.StreamEntry("1696000000123-0", []);

        var mapped = RedisStreamEntryMapper.ToStreamEntry(entry);

        mapped.Fields.Should().BeEmpty();
    }
}
