using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Plugins.Redis.Streams;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis.Tests.Handlers;

public class StreamsCommandHandlerTests : HandlerTestBase
{
    private const string Conn = "redis-uk-prod";
    private const string Key = "orders:events";

    public static TheoryData<string> SimpleCommands => new() { "stream.trim", "stream.ack", "stream.delete", "stream.consumer.delete" };

    private (Func<Task<PluginResult>> Run, string Action, string Target, ActionRisk Risk) Case(string name) => name switch
    {
        "stream.trim" => (() => new TrimStreamCommandHandler(Operations, Connections, Audit, Log<TrimStreamCommandHandler>())
                .HandleAsync(new(ConnectionId, Conn, Database, Key, 1000)),
            "redis.stream.trim", $"{Conn}/db{Database}/{Key}", ActionRisk.Destructive),
        "stream.ack" => (() => new AcknowledgeStreamEntriesCommandHandler(Operations, Connections, Audit, Log<AcknowledgeStreamEntriesCommandHandler>())
                .HandleAsync(new(ConnectionId, Conn, Database, Key, "workers", ["1696000000000-0"])),
            "redis.stream.ack", $"{Conn}/db{Database}/{Key}", ActionRisk.Mutating),
        "stream.delete" => (() => new DeleteStreamEntriesCommandHandler(Operations, Connections, Audit, Log<DeleteStreamEntriesCommandHandler>())
                .HandleAsync(new(ConnectionId, Conn, Database, Key, ["1696000000000-0"])),
            "redis.stream.delete", $"{Conn}/db{Database}/{Key}", ActionRisk.Destructive),
        "stream.consumer.delete" => (() => new DeleteStreamConsumerCommandHandler(Operations, Connections, Audit, Log<DeleteStreamConsumerCommandHandler>())
                .HandleAsync(new(ConnectionId, Conn, Database, Key, "billing", "billing-2a41")),
            "redis.stream.consumer.delete", $"{Conn}/db{Database}/{Key}", ActionRisk.Destructive),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(SimpleCommands))]
    public async Task Success_calls_redis_and_audits(string name)
    {
        var (run, action, target, risk) = Case(name);

        var result = await run();

        result.IsSuccess.Should().BeTrue();
        Operations.ReceivedCalls().Should().ContainSingle();
        await Audit.Received(1).RecordAsync(action, target, risk, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [MemberData(nameof(SimpleCommands))]
    public async Task Failure_returns_the_friendly_error_and_audits_it(string name)
    {
        var (run, action, target, risk) = Case(name);
        ThrowOnEveryWrite(Timeout);

        var result = await run();

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(TimeoutText);
        await Audit.Received(1).RecordAsync(action, target, risk, false, TimeoutText, Arg.Any<CancellationToken>());
    }

    [Theory]
    [MemberData(nameof(SimpleCommands))]
    public async Task Missing_connection_fails_without_a_redis_call_or_audit(string name)
    {
        Connections.GetSecretAsync(ConnectionId, Arg.Any<CancellationToken>()).Returns((string?)null);
        var (run, _, _, _) = Case(name);

        var result = await run();

        result.Error.Should().Be("Connection not found.");
        Operations.ReceivedCalls().Should().BeEmpty();
        Audit.ReceivedCalls().Should().BeEmpty();
    }

    private void ThrowOnEveryWrite(Exception ex)
    {
        Operations.TrimStreamAsync(default!, default, default!, default, default).ThrowsAsyncForAnyArgs(ex);
        Operations.AcknowledgeStreamEntriesAsync(default!, default, default!, default!, default!, default).ThrowsAsyncForAnyArgs(ex);
        Operations.DeleteStreamEntriesAsync(default!, default, default!, default!, default).ThrowsAsyncForAnyArgs(ex);
        Operations.DeleteStreamConsumerAsync(default!, default, default!, default!, default!, default).ThrowsAsyncForAnyArgs(ex);
    }

    [Fact]
    public async Task Delete_consumer_details_the_group_and_consumer()
    {
        await new DeleteStreamConsumerCommandHandler(Operations, Connections, Audit, Log<DeleteStreamConsumerCommandHandler>())
            .HandleAsync(new(ConnectionId, Conn, Database, Key, "billing", "billing-2a41"));

        await Audit.Received(1).RecordAsync("redis.stream.consumer.delete", $"{Conn}/db{Database}/{Key}", ActionRisk.Destructive, true,
            "group billing: consumer billing-2a41", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Trim_details_the_maxlen()
    {
        await new TrimStreamCommandHandler(Operations, Connections, Audit, Log<TrimStreamCommandHandler>())
            .HandleAsync(new(ConnectionId, Conn, Database, Key, 1000));

        await Audit.Received(1).RecordAsync("redis.stream.trim", $"{Conn}/db{Database}/{Key}", ActionRisk.Destructive, true,
            "maxlen 1000", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Ack_details_the_group_and_id_count()
    {
        await new AcknowledgeStreamEntriesCommandHandler(Operations, Connections, Audit, Log<AcknowledgeStreamEntriesCommandHandler>())
            .HandleAsync(new(ConnectionId, Conn, Database, Key, "workers", ["1696000000000-0", "1696000000001-0"]));

        await Audit.Received(1).RecordAsync("redis.stream.ack", $"{Conn}/db{Database}/{Key}", ActionRisk.Mutating, true,
            "group workers: 2 id(s)", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_details_the_id_count()
    {
        await new DeleteStreamEntriesCommandHandler(Operations, Connections, Audit, Log<DeleteStreamEntriesCommandHandler>())
            .HandleAsync(new(ConnectionId, Conn, Database, Key, ["1696000000000-0"]));

        await Audit.Received(1).RecordAsync("redis.stream.delete", $"{Conn}/db{Database}/{Key}", ActionRisk.Destructive, true,
            "1 id(s)", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Add_entry_returns_the_new_id_and_audits_it()
    {
        Operations.AddStreamEntryAsync(Secret, Database, Key, Arg.Any<IReadOnlyList<StreamFieldValue>>(), null, Arg.Any<CancellationToken>())
            .Returns("1696000000123-0");

        var result = await new AddStreamEntryCommandHandler(Operations, Connections, Audit, Log<AddStreamEntryCommandHandler>())
            .HandleAsync(new(ConnectionId, Conn, Database, Key, [new StreamFieldValue("event", "order.created")]));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("1696000000123-0");
        await Audit.Received(1).RecordAsync("redis.stream.add", $"{Conn}/db{Database}/{Key}", ActionRisk.Mutating, true,
            "1696000000123-0", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Add_entry_failure_is_friendly_and_audited()
    {
        Operations.AddStreamEntryAsync(Secret, Database, Key, Arg.Any<IReadOnlyList<StreamFieldValue>>(), null, Arg.Any<CancellationToken>())
            .ThrowsAsync(Timeout);

        var result = await new AddStreamEntryCommandHandler(Operations, Connections, Audit, Log<AddStreamEntryCommandHandler>())
            .HandleAsync(new(ConnectionId, Conn, Database, Key, [new StreamFieldValue("event", "order.created")]));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(TimeoutText);
        await Audit.Received(1).RecordAsync("redis.stream.add", $"{Conn}/db{Database}/{Key}", ActionRisk.Mutating, false, TimeoutText, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Add_entry_missing_connection_fails_without_a_redis_call_or_audit()
    {
        Connections.GetSecretAsync(ConnectionId, Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await new AddStreamEntryCommandHandler(Operations, Connections, Audit, Log<AddStreamEntryCommandHandler>())
            .HandleAsync(new(ConnectionId, Conn, Database, Key, [new StreamFieldValue("event", "order.created")]));

        result.Error.Should().Be("Connection not found.");
        Operations.ReceivedCalls().Should().BeEmpty();
        Audit.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Claim_returns_the_claimed_entries_and_audits_the_requested_and_claimed_counts()
    {
        IReadOnlyList<StreamEntry> claimed = [new StreamEntry("1696000000000-0", [new StreamFieldValue("event", "order.created")])];
        Operations.ClaimStreamEntriesAsync(Secret, Database, Key, "workers", "worker-2", 60000, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(claimed);

        var result = await new ClaimStreamEntriesCommandHandler(Operations, Connections, Audit, Log<ClaimStreamEntriesCommandHandler>())
            .HandleAsync(new(ConnectionId, Conn, Database, Key, "workers", "worker-2", 60000, ["1696000000000-0"]));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeSameAs(claimed);
        await Audit.Received(1).RecordAsync("redis.stream.claim", $"{Conn}/db{Database}/{Key}", ActionRisk.Mutating, true,
            Arg.Is<string>(d => d.Contains("workers -> worker-2") && d.Contains("1 id(s) requested") && d.Contains("1 claimed")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Claim_failure_is_friendly_and_audited()
    {
        Operations.ClaimStreamEntriesAsync(Secret, Database, Key, "workers", "worker-2", 60000, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(Timeout);

        var result = await new ClaimStreamEntriesCommandHandler(Operations, Connections, Audit, Log<ClaimStreamEntriesCommandHandler>())
            .HandleAsync(new(ConnectionId, Conn, Database, Key, "workers", "worker-2", 60000, ["1696000000000-0"]));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(TimeoutText);
        await Audit.Received(1).RecordAsync("redis.stream.claim", $"{Conn}/db{Database}/{Key}", ActionRisk.Mutating, false, TimeoutText, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Claim_missing_connection_fails_without_a_redis_call_or_audit()
    {
        Connections.GetSecretAsync(ConnectionId, Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await new ClaimStreamEntriesCommandHandler(Operations, Connections, Audit, Log<ClaimStreamEntriesCommandHandler>())
            .HandleAsync(new(ConnectionId, Conn, Database, Key, "workers", "worker-2", 60000, ["1696000000000-0"]));

        result.Error.Should().Be("Connection not found.");
        Operations.ReceivedCalls().Should().BeEmpty();
        Audit.ReceivedCalls().Should().BeEmpty();
    }
}
