using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Plugins.Redis.Keys;

namespace SbConsole.Plugins.Redis.Tests.Handlers;

public class QueryHandlerTests : HandlerTestBase
{
    private const string Key = "session:42";

    private KeysQueryHandler Handler => new(Operations, Connections, Log<KeysQueryHandler>());

    [Fact]
    public async Task Scan_returns_the_page_from_operations()
    {
        var page = new KeyScanPage([new KeySummary(Key, RedisKeyType.Hash, TimeSpan.FromMinutes(5), 128)], 0, true, 1000, 2000);
        var request = new ScanKeysRequest(Database, "session:*", null, 0, 1000);
        Operations.ScanKeysAsync(Secret, request, Arg.Any<CancellationToken>()).Returns(page);

        var result = await Handler.ScanAsync(ConnectionId, request);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(page);
    }

    [Fact]
    public async Task Scan_failure_is_friendly()
    {
        var request = new ScanKeysRequest(Database, "session:*", null, 0, 1000);
        Operations.ScanKeysAsync(Secret, request, Arg.Any<CancellationToken>()).ThrowsAsync(Timeout);

        var result = await Handler.ScanAsync(ConnectionId, request);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(TimeoutText);
    }

    [Fact]
    public async Task Missing_connection_fails_without_a_redis_call()
    {
        Connections.GetSecretAsync(ConnectionId, Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await Handler.GetTypeAsync(ConnectionId, Database, Key);

        result.Error.Should().Be("Connection not found.");
        Operations.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task GetType_returns_the_type_from_operations()
    {
        Operations.GetKeyTypeAsync(Secret, Database, Key, Arg.Any<CancellationToken>()).Returns(RedisKeyType.Hash);

        var result = await Handler.GetTypeAsync(ConnectionId, Database, Key);

        result.Value.Should().Be(RedisKeyType.Hash);
    }

    [Fact]
    public async Task GetString_returns_the_value_from_operations()
    {
        var value = new StringValue(Key, null, 12, "hello", false);
        Operations.GetStringAsync(Secret, Database, Key, Arg.Any<CancellationToken>()).Returns(value);

        var result = await Handler.GetStringAsync(ConnectionId, Database, Key);

        result.Value.Should().Be(value);
    }

    [Fact]
    public async Task ScanHashFields_returns_the_page_from_operations()
    {
        var page = new HashFieldPage([new HashFieldEntry("csrf", "abc", 3)], 0, true, 1);
        Operations.ScanHashFieldsAsync(Secret, Database, Key, "*", 0, 100, Arg.Any<CancellationToken>()).Returns(page);

        var result = await Handler.ScanHashFieldsAsync(ConnectionId, Database, Key, "*", 0, 100);

        result.Value.Should().Be(page);
    }

    [Fact]
    public async Task GetListSlice_returns_the_slice_from_operations()
    {
        var slice = new ListSlice(10, [new ListEntry(0, "a")], [new ListEntry(-1, "z")]);
        Operations.GetListSliceAsync(Secret, Database, "jobs:queue", 10, 10, Arg.Any<CancellationToken>()).Returns(slice);

        var result = await Handler.GetListSliceAsync(ConnectionId, Database, "jobs:queue", 10, 10);

        result.Value.Should().Be(slice);
    }

    [Fact]
    public async Task GetSortedSetRange_returns_the_range_from_operations()
    {
        var slice = new SortedSetSlice(3, [new SortedSetEntry("p1", 42.0)], 0, 9);
        Operations.GetSortedSetRangeAsync(Secret, Database, "lb:us-east", 0, 9, true, Arg.Any<CancellationToken>()).Returns(slice);

        var result = await Handler.GetSortedSetRangeAsync(ConnectionId, Database, "lb:us-east", 0, 9, descending: true);

        result.Value.Should().Be(slice);
    }

    [Fact]
    public async Task ScanSetMembers_returns_the_page_from_operations()
    {
        var page = new SetPage(2, ["user-1", "user-2"], 0, true);
        Operations.ScanSetMembersAsync(Secret, Database, "flags:beta", 0, 100, Arg.Any<CancellationToken>()).Returns(page);

        var result = await Handler.ScanSetMembersAsync(ConnectionId, Database, "flags:beta", 0, 100);

        result.Value.Should().Be(page);
    }

    [Fact]
    public async Task DryRunDeleteByPattern_returns_the_result_from_operations()
    {
        var dryRun = new DryRunDeleteByPatternResult(142, 4096, true);
        Operations.DryRunDeleteByPatternAsync(Secret, Database, "session:*", Arg.Any<CancellationToken>()).Returns(dryRun);

        var result = await Handler.DryRunDeleteByPatternAsync(ConnectionId, Database, "session:*");

        result.Value.Should().Be(dryRun);
    }

    [Fact]
    public async Task GetKeyMetadata_returns_encoding_and_idle_time_from_operations()
    {
        var metadata = new KeyMetadata("hashtable", TimeSpan.FromSeconds(3));
        Operations.GetKeyMetadataAsync(Secret, Database, "session:u:77120", Arg.Any<CancellationToken>()).Returns(metadata);

        var result = await Handler.GetKeyMetadataAsync(ConnectionId, Database, "session:u:77120");

        result.Value.Should().Be(metadata);
    }

    [Fact]
    public async Task GetKeyMetadata_failure_is_friendly()
    {
        Operations.GetKeyMetadataAsync(Secret, Database, "k", Arg.Any<CancellationToken>()).ThrowsAsync(Timeout);

        var result = await Handler.GetKeyMetadataAsync(ConnectionId, Database, "k");

        result.Error.Should().Be(TimeoutText);
    }

    [Fact]
    public async Task IsSetMember_returns_the_answer_from_operations()
    {
        Operations.IsSetMemberAsync(Secret, Database, "flags:beta", "acme-uk", Arg.Any<CancellationToken>()).Returns(true);

        var result = await Handler.IsSetMemberAsync(ConnectionId, Database, "flags:beta", "acme-uk");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeTrue();
    }

    [Fact]
    public async Task IsSetMember_failure_is_friendly()
    {
        Operations.IsSetMemberAsync(Secret, Database, "k", "m", Arg.Any<CancellationToken>()).ThrowsAsync(Timeout);

        var result = await Handler.IsSetMemberAsync(ConnectionId, Database, "k", "m");

        result.Error.Should().Be(TimeoutText);
    }
}
