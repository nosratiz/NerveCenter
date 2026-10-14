namespace SbConsole.Plugins.Redis.Client;

/// <summary>
/// Pure StackExchange.Redis.StreamEntry/NameValueEntry -&gt; our own StreamEntry/StreamFieldValue
/// mapping. Unlike most of the other Stream reply types (StreamInfo, StreamGroupInfo,
/// StreamConsumerInfo, StreamPendingMessageInfo -- all have non-public constructors, so a fixture
/// can't build one directly), StreamEntry and NameValueEntry have PUBLIC constructors, so this one
/// mapping step is unit-testable against real library types without a live server. The others'
/// mapping stays inline in RedisOperations, untested, same as the rest of that thin wrapper.
/// </summary>
public static class RedisStreamEntryMapper
{
    public static StreamEntry ToStreamEntry(StackExchange.Redis.StreamEntry entry) =>
        new(entry.Id.ToString(), entry.Values.Select(v => new StreamFieldValue(v.Name.ToString(), v.Value.ToString())).ToList());
}
