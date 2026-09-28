using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Plugins.Redis.Keys;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis.Tests.Handlers;

public class CommandHandlerTests : HandlerTestBase
{
    private const string Conn = "redis-uk-prod";
    private const string Key = "session:42";

    public static TheoryData<string> SimpleCommands => new()
    {
        "key.delete", "key.rename", "key.setttl.persist", "key.setttl.expirein", "key.setttl.expireat",
        "string.set", "hash.setfield", "hash.deletefield", "list.push", "list.trim",
        "zset.add", "set.add", "set.remove",
    };

    // (run the handler, expected audit action, target, risk, detail on success)
    private (Func<Task<PluginResult>> Run, string Action, string Target, ActionRisk Risk, string? Detail) Case(string name) => name switch
    {
        "key.delete" => (() => new DeleteKeyCommandHandler(Operations, Connections, Audit, Log<DeleteKeyCommandHandler>())
                .HandleAsync(new(ConnectionId, Conn, Database, Key)),
            "redis.key.delete", $"{Conn}/db{Database}/{Key}", ActionRisk.Destructive, null),
        "key.rename" => (() => new RenameKeyCommandHandler(Operations, Connections, Audit, Log<RenameKeyCommandHandler>())
                .HandleAsync(new(ConnectionId, Conn, Database, Key, "session:43")),
            "redis.key.rename", $"{Conn}/db{Database}/{Key}", ActionRisk.Mutating, "session:43"),
        "key.setttl.persist" => (() => new SetTtlCommandHandler(Operations, Connections, Audit, Log<SetTtlCommandHandler>())
                .HandleAsync(new(ConnectionId, Conn, new SetTtlRequest(Database, Key, null, null, Persist: true))),
            "redis.key.setttl", $"{Conn}/db{Database}/{Key}", ActionRisk.Mutating, "persist"),
        "key.setttl.expirein" => (() => new SetTtlCommandHandler(Operations, Connections, Audit, Log<SetTtlCommandHandler>())
                .HandleAsync(new(ConnectionId, Conn, new SetTtlRequest(Database, Key, TimeSpan.FromMinutes(30), null, false))),
            "redis.key.setttl", $"{Conn}/db{Database}/{Key}", ActionRisk.Mutating, $"expires in {TimeSpan.FromMinutes(30)}"),
        "key.setttl.expireat" => (() => new SetTtlCommandHandler(Operations, Connections, Audit, Log<SetTtlCommandHandler>())
                .HandleAsync(new(ConnectionId, Conn, new SetTtlRequest(Database, Key, null, new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero), false))),
            "redis.key.setttl", $"{Conn}/db{Database}/{Key}", ActionRisk.Mutating, $"expires at {new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero):O}"),
        "string.set" => (() => new SetStringCommandHandler(Operations, Connections, Audit, Log<SetStringCommandHandler>())
                .HandleAsync(new(ConnectionId, Conn, Database, Key, "hello")),
            "redis.string.set", $"{Conn}/db{Database}/{Key}", ActionRisk.Mutating, null),
        "hash.setfield" => (() => new SetHashFieldCommandHandler(Operations, Connections, Audit, Log<SetHashFieldCommandHandler>())
                .HandleAsync(new(ConnectionId, Conn, Database, Key, "csrf", "abc123")),
            "redis.hash.setfield", $"{Conn}/db{Database}/{Key}", ActionRisk.Mutating, "csrf"),
        "hash.deletefield" => (() => new DeleteHashFieldCommandHandler(Operations, Connections, Audit, Log<DeleteHashFieldCommandHandler>())
                .HandleAsync(new(ConnectionId, Conn, Database, Key, "csrf")),
            "redis.hash.deletefield", $"{Conn}/db{Database}/{Key}", ActionRisk.Mutating, "csrf"),
        "list.push" => (() => new PushListCommandHandler(Operations, Connections, Audit, Log<PushListCommandHandler>())
                .HandleAsync(new(ConnectionId, Conn, Database, "jobs:queue", "job-9", AtHead: false)),
            "redis.list.push", $"{Conn}/db{Database}/jobs:queue", ActionRisk.Mutating, "tail"),
        "list.trim" => (() => new TrimListCommandHandler(Operations, Connections, Audit, Log<TrimListCommandHandler>())
                .HandleAsync(new(ConnectionId, Conn, Database, "jobs:queue", 0, 99)),
            "redis.list.trim", $"{Conn}/db{Database}/jobs:queue", ActionRisk.Destructive, "keep 0..99"),
        "zset.add" => (() => new AddSortedSetMemberCommandHandler(Operations, Connections, Audit, Log<AddSortedSetMemberCommandHandler>())
                .HandleAsync(new(ConnectionId, Conn, Database, "lb:us-east", "player-1", 42.5)),
            "redis.zset.add", $"{Conn}/db{Database}/lb:us-east", ActionRisk.Mutating, "player-1 score 42.5"),
        "set.add" => (() => new AddSetMemberCommandHandler(Operations, Connections, Audit, Log<AddSetMemberCommandHandler>())
                .HandleAsync(new(ConnectionId, Conn, Database, "flags:beta", "user-1")),
            "redis.set.add", $"{Conn}/db{Database}/flags:beta", ActionRisk.Mutating, "user-1"),
        "set.remove" => (() => new RemoveSetMemberCommandHandler(Operations, Connections, Audit, Log<RemoveSetMemberCommandHandler>())
                .HandleAsync(new(ConnectionId, Conn, Database, "flags:beta", "user-1")),
            "redis.set.remove", $"{Conn}/db{Database}/flags:beta", ActionRisk.Mutating, "user-1"),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(SimpleCommands))]
    public async Task Success_calls_redis_and_audits(string name)
    {
        var (run, action, target, risk, detail) = Case(name);

        var result = await run();

        result.IsSuccess.Should().BeTrue();
        Operations.ReceivedCalls().Should().ContainSingle();
        await Audit.Received(1).RecordAsync(action, target, risk, true, detail, Arg.Any<CancellationToken>());
    }

    [Theory]
    [MemberData(nameof(SimpleCommands))]
    public async Task Failure_returns_the_friendly_error_and_audits_it(string name)
    {
        var (run, action, target, risk, _) = Case(name);
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
        var (run, _, _, _, _) = Case(name);

        var result = await run();

        result.Error.Should().Be("Connection not found.");
        Operations.ReceivedCalls().Should().BeEmpty();
        Audit.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(SimpleCommands))]
    public async Task A_failed_success_audit_still_reports_success(string name)
    {
        var (run, action, target, risk, _) = Case(name);
        Audit.RecordAsync(default!, default!, default, default, default, default).ThrowsAsyncForAnyArgs(new InvalidOperationException("db locked"));

        var result = await run();

        result.IsSuccess.Should().BeTrue();
        await Audit.DidNotReceive().RecordAsync(action, target, risk, false, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Command_audits_are_written_with_CancellationToken_None()
    {
        using var cts = new CancellationTokenSource();

        await new DeleteKeyCommandHandler(Operations, Connections, Audit, Log<DeleteKeyCommandHandler>())
            .HandleAsync(new(ConnectionId, Conn, Database, Key), cts.Token);

        await Audit.Received(1).RecordAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ActionRisk>(), true, Arg.Any<string?>(), CancellationToken.None);
    }

    private void ThrowOnEveryWrite(Exception ex)
    {
        Operations.DeleteKeyAsync(default!, default, default!, default).ThrowsAsyncForAnyArgs(ex);
        Operations.RenameKeyAsync(default!, default, default!, default!, default).ThrowsAsyncForAnyArgs(ex);
        Operations.SetTtlAsync(default!, default!, default).ThrowsAsyncForAnyArgs(ex);
        Operations.SetStringAsync(default!, default, default!, default!, default, default).ThrowsAsyncForAnyArgs(ex);
        Operations.SetHashFieldAsync(default!, default, default!, default!, default!, default).ThrowsAsyncForAnyArgs(ex);
        Operations.DeleteHashFieldAsync(default!, default, default!, default!, default).ThrowsAsyncForAnyArgs(ex);
        Operations.PushListAsync(default!, default, default!, default!, default, default).ThrowsAsyncForAnyArgs(ex);
        Operations.TrimListAsync(default!, default, default!, default, default, default).ThrowsAsyncForAnyArgs(ex);
        Operations.AddSortedSetMemberAsync(default!, default, default!, default!, default, default).ThrowsAsyncForAnyArgs(ex);
        Operations.AddSetMemberAsync(default!, default, default!, default!, default).ThrowsAsyncForAnyArgs(ex);
        Operations.RemoveSetMemberAsync(default!, default, default!, default!, default).ThrowsAsyncForAnyArgs(ex);
    }

    [Fact]
    public async Task Delete_by_pattern_returns_the_deleted_count_and_folds_the_dry_run_count_into_the_detail()
    {
        Operations.DeleteByPatternAsync(Secret, Database, "session:*", Arg.Any<CancellationToken>()).Returns(142L);

        var result = await new DeleteByPatternCommandHandler(Operations, Connections, Audit, Log<DeleteByPatternCommandHandler>())
            .HandleAsync(new(ConnectionId, Conn, Database, "session:*", DryRunKeyCount: 142));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(142);
        await Audit.Received(1).RecordAsync("redis.pattern.delete", $"{Conn}/db{Database}/session:*", ActionRisk.Destructive, true,
            Arg.Is<string>(d => d.Contains("142") && d.Contains("dry run")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_by_pattern_failure_is_friendly_and_audited()
    {
        Operations.DeleteByPatternAsync(Secret, Database, "session:*", Arg.Any<CancellationToken>()).ThrowsAsync(Timeout);

        var result = await new DeleteByPatternCommandHandler(Operations, Connections, Audit, Log<DeleteByPatternCommandHandler>())
            .HandleAsync(new(ConnectionId, Conn, Database, "session:*"));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(TimeoutText);
        await Audit.Received(1).RecordAsync("redis.pattern.delete", $"{Conn}/db{Database}/session:*", ActionRisk.Destructive, false, TimeoutText, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Increment_sorted_set_score_returns_the_new_score_and_audits()
    {
        Operations.IncrementSortedSetScoreAsync(Secret, Database, "lb:us-east", "player-1", 5.0, Arg.Any<CancellationToken>()).Returns(47.5);

        var result = await new IncrementSortedSetScoreCommandHandler(Operations, Connections, Audit, Log<IncrementSortedSetScoreCommandHandler>())
            .HandleAsync(new(ConnectionId, Conn, Database, "lb:us-east", "player-1", 5.0));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(47.5);
        await Audit.Received(1).RecordAsync("redis.zset.incr", $"{Conn}/db{Database}/lb:us-east", ActionRisk.Mutating, true,
            Arg.Is<string>(d => d.Contains("player-1") && d.Contains("47.5")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Increment_sorted_set_score_failure_is_friendly_and_audited()
    {
        Operations.IncrementSortedSetScoreAsync(Secret, Database, "lb:us-east", "player-1", 5.0, Arg.Any<CancellationToken>()).ThrowsAsync(Timeout);

        var result = await new IncrementSortedSetScoreCommandHandler(Operations, Connections, Audit, Log<IncrementSortedSetScoreCommandHandler>())
            .HandleAsync(new(ConnectionId, Conn, Database, "lb:us-east", "player-1", 5.0));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(TimeoutText);
        await Audit.Received(1).RecordAsync("redis.zset.incr", $"{Conn}/db{Database}/lb:us-east", ActionRisk.Mutating, false, TimeoutText, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reveal_hash_field_writes_a_safe_audit_row_with_no_operations_call()
    {
        await new RevealHashFieldCommandHandler(Audit, Log<RevealHashFieldCommandHandler>())
            .RecordAsync(new(ConnectionId, Conn, Database, Key, "csrf"));

        Operations.ReceivedCalls().Should().BeEmpty();
        await Audit.Received(1).RecordAsync("redis.field.reveal", $"{Conn}/db{Database}/{Key}.csrf", ActionRisk.Safe, true, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Every_handler_resolves_after_ConfigureServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Connections);
        services.AddSingleton(Audit);
        new RedisPlugin().ConfigureServices(services);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using var scope = provider.CreateScope();

        var handlerTypes = typeof(RedisPlugin).Assembly.GetTypes()
            .Where(t => t.IsPublic && (t.Name.EndsWith("QueryHandler", StringComparison.Ordinal) || t.Name.EndsWith("CommandHandler", StringComparison.Ordinal)))
            .ToList();

        handlerTypes.Should().HaveCountGreaterThan(10);
        foreach (var type in handlerTypes)
        {
            scope.ServiceProvider.GetService(type).Should().NotBeNull($"{type.Name} must be registered");
        }
    }
}
