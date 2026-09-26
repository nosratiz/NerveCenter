using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.RabbitMq.Bindings;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Plugins.RabbitMq.Exchanges;
using SbConsole.Plugins.RabbitMq.Messages;
using SbConsole.Plugins.RabbitMq.Queues;
using SbConsole.Plugins.RabbitMq.Shovels;
using SbConsole.Sdk;

namespace SbConsole.Plugins.RabbitMq.Tests.Handlers;

public class CommandHandlerTests : HandlerTestBase
{
    private const string Conn = "rabbit-uk-prod";

    public static TheoryData<string> SimpleCommands => new()
    {
        "exchange.create", "exchange.delete", "queue.create", "queue.delete", "queue.purge",
        "binding.add", "binding.remove", "shovel.create", "shovel.delete", "shovel.restart",
    };

    // (run the handler, expected audit action, target, risk, detail on success)
    private (Func<Task<PluginResult>> Run, string Action, string Target, ActionRisk Risk, string? Detail) Case(string name) => name switch
    {
        "exchange.create" => (() => new CreateExchangeCommandHandler(Operations, Connections, Audit, Log<CreateExchangeCommandHandler>())
                .HandleAsync(new(ConnectionId, Conn, Vhost, new CreateExchangeRequest("order-events", "topic"))),
            "rabbitmq.exchange.create", "rabbit-uk-prod//orders/order-events", ActionRisk.Mutating, "topic"),
        "exchange.delete" => (() => new DeleteExchangeCommandHandler(Operations, Connections, Audit, Log<DeleteExchangeCommandHandler>())
                .HandleAsync(new(ConnectionId, Conn, Vhost, "legacy.import")),
            "rabbitmq.exchange.delete", "rabbit-uk-prod//orders/legacy.import", ActionRisk.Destructive, null),
        "queue.create" => (() => new CreateQueueCommandHandler(Operations, Connections, Audit, Log<CreateQueueCommandHandler>())
                .HandleAsync(new(ConnectionId, Conn, Vhost, new CreateQueueRequest("audit.sink", "quorum"))),
            "rabbitmq.queue.create", "rabbit-uk-prod//orders/audit.sink", ActionRisk.Mutating, "quorum"),
        "queue.delete" => (() => new DeleteQueueCommandHandler(Operations, Connections, Audit, Log<DeleteQueueCommandHandler>())
                .HandleAsync(new(ConnectionId, Conn, Vhost, "legacy.import.q")),
            "rabbitmq.queue.delete", "rabbit-uk-prod//orders/legacy.import.q", ActionRisk.Destructive, null),
        "queue.purge" => (() => new PurgeQueueCommandHandler(Operations, Connections, Audit, Log<PurgeQueueCommandHandler>())
                .HandleAsync(new(ConnectionId, Conn, Vhost, "payments-dlq", 214)),
            "rabbitmq.queue.purge", "rabbit-uk-prod//orders/payments-dlq", ActionRisk.Destructive, "214 ready"),
        "binding.add" => (() => new AddBindingCommandHandler(Operations, Connections, Audit, Log<AddBindingCommandHandler>())
                .HandleAsync(new(ConnectionId, Conn, Vhost, "order-events", "order-events.q", "order.*.created")),
            "rabbitmq.binding.add", "rabbit-uk-prod//orders/order-events→order-events.q", ActionRisk.Mutating, "key order.*.created"),
        "binding.remove" => (() => new RemoveBindingCommandHandler(Operations, Connections, Audit, Log<RemoveBindingCommandHandler>())
                .HandleAsync(new(ConnectionId, Conn, Vhost, "order-events", "order-events.q", "order.*.created", "order.%2A.created")),
            "rabbitmq.binding.remove", "rabbit-uk-prod//orders/order-events→order-events.q", ActionRisk.Mutating, "key order.*.created"),
        "shovel.create" => (() => new CreateShovelCommandHandler(Operations, Connections, Audit, Log<CreateShovelCommandHandler>())
                .HandleAsync(new(ConnectionId, Conn, Vhost, new CreateShovelRequest("dlq-drain", "payments-dlq", null, "order-events", null))),
            "rabbitmq.shovel.create", "rabbit-uk-prod//orders/dlq-drain", ActionRisk.Mutating, null),
        "shovel.delete" => (() => new DeleteShovelCommandHandler(Operations, Connections, Audit, Log<DeleteShovelCommandHandler>())
                .HandleAsync(new(ConnectionId, Conn, Vhost, "dlq-drain")),
            "rabbitmq.shovel.delete", "rabbit-uk-prod//orders/dlq-drain", ActionRisk.Destructive, null),
        "shovel.restart" => (() => new RestartShovelCommandHandler(Operations, Connections, Audit, Log<RestartShovelCommandHandler>())
                .HandleAsync(new(ConnectionId, Conn, Vhost, "dlq-drain")),
            "rabbitmq.shovel.restart", "rabbit-uk-prod//orders/dlq-drain", ActionRisk.Mutating, null),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(SimpleCommands))]
    public async Task Success_calls_the_broker_and_audits(string name)
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
        ThrowOnEveryWrite(Forbidden);

        var result = await run();

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(ForbiddenText);
        await Audit.Received(1).RecordAsync(action, target, risk, false, ForbiddenText, Arg.Any<CancellationToken>());
    }

    [Theory]
    [MemberData(nameof(SimpleCommands))]
    public async Task Missing_connection_fails_without_broker_call_or_audit(string name)
    {
        Connections.GetSecretAsync(ConnectionId, Arg.Any<CancellationToken>()).Returns((string?)null);
        var (run, _, _, _, _) = Case(name);

        var result = await run();

        result.Error.Should().Be("Connection not found.");
        Operations.ReceivedCalls().Should().BeEmpty();
        Audit.ReceivedCalls().Should().BeEmpty();
    }

    private void ThrowOnEveryWrite(Exception ex)
    {
        Operations.CreateExchangeAsync(default!, default!, default!, default).ThrowsAsyncForAnyArgs(ex);
        Operations.DeleteExchangeAsync(default!, default!, default!, default).ThrowsAsyncForAnyArgs(ex);
        Operations.CreateQueueAsync(default!, default!, default!, default).ThrowsAsyncForAnyArgs(ex);
        Operations.DeleteQueueAsync(default!, default!, default!, default).ThrowsAsyncForAnyArgs(ex);
        Operations.PurgeQueueAsync(default!, default!, default!, default).ThrowsAsyncForAnyArgs(ex);
        Operations.AddBindingAsync(default!, default!, default!, default!, default!, default!, default).ThrowsAsyncForAnyArgs(ex);
        Operations.RemoveBindingAsync(default!, default!, default!, default!, default!, default).ThrowsAsyncForAnyArgs(ex);
        Operations.CreateShovelAsync(default!, default!, default!, default).ThrowsAsyncForAnyArgs(ex);
        Operations.DeleteShovelAsync(default!, default!, default!, default).ThrowsAsyncForAnyArgs(ex);
        Operations.RestartShovelAsync(default!, default!, default!, default).ThrowsAsyncForAnyArgs(ex);
    }

    private static PublishRequest Message(string key = "order.uk.created") => new("order-events", key, [1]);

    [Fact]
    public async Task Publish_routed_is_audited_as_success()
    {
        Operations.PublishAsync(Secret, Vhost, Arg.Any<PublishRequest>(), Arg.Any<CancellationToken>()).Returns(PublishOutcome.Routed);

        var result = await new PublishMessageCommandHandler(Operations, Connections, Audit, Log<PublishMessageCommandHandler>())
            .HandleAsync(new(ConnectionId, Conn, Vhost, Message()));

        result.Value.Should().Be(PublishOutcome.Routed);
        await Audit.Received(1).RecordAsync("rabbitmq.message.publish", "rabbit-uk-prod//orders/order-events", ActionRisk.Mutating, true, "key order.uk.created", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Publish_unroutable_is_ok_but_audited_as_failed()
    {
        Operations.PublishAsync(Secret, Vhost, Arg.Any<PublishRequest>(), Arg.Any<CancellationToken>()).Returns(PublishOutcome.Unroutable);

        var result = await new PublishMessageCommandHandler(Operations, Connections, Audit, Log<PublishMessageCommandHandler>())
            .HandleAsync(new(ConnectionId, Conn, Vhost, Message("order.created")));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(PublishOutcome.Unroutable);
        await Audit.Received(1).RecordAsync("rabbitmq.message.publish", Arg.Any<string>(), ActionRisk.Mutating, false,
            Arg.Is<string>(d => d.Contains("unroutable")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Publish_to_the_default_exchange_names_it_in_the_target()
    {
        Operations.PublishAsync(Secret, Vhost, Arg.Any<PublishRequest>(), Arg.Any<CancellationToken>()).Returns(PublishOutcome.Routed);

        await new PublishMessageCommandHandler(Operations, Connections, Audit, Log<PublishMessageCommandHandler>())
            .HandleAsync(new(ConnectionId, Conn, Vhost, new PublishRequest("", "payments-dlq", [1])));

        await Audit.Received(1).RecordAsync(Arg.Any<string>(), "rabbit-uk-prod//orders/(default)", Arg.Any<ActionRisk>(), true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Publish_failure_is_friendly_and_audited()
    {
        Operations.PublishAsync(Secret, Vhost, Arg.Any<PublishRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(Forbidden);

        var result = await new PublishMessageCommandHandler(Operations, Connections, Audit, Log<PublishMessageCommandHandler>())
            .HandleAsync(new(ConnectionId, Conn, Vhost, Message()));

        result.Error.Should().Be(ForbiddenText);
        await Audit.Received(1).RecordAsync(Arg.Any<string>(), Arg.Any<string>(), ActionRisk.Mutating, false, ForbiddenText, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Republish_counts_outcomes_and_writes_one_audit_row()
    {
        Operations.PublishAsync(Secret, Vhost, Arg.Any<PublishRequest>(), Arg.Any<CancellationToken>())
            .Returns(PublishOutcome.Routed, PublishOutcome.Unroutable, PublishOutcome.Routed);

        var result = await new RepublishMessagesCommandHandler(Operations, Connections, Audit, Log<RepublishMessagesCommandHandler>())
            .HandleAsync(new(ConnectionId, Conn, Vhost, "payments-dlq", "republish", [Message(), Message(), Message()]));

        result.Value.Should().Be(new RepublishResult(2, 1));
        await Audit.Received(1).RecordAsync("rabbitmq.message.publish", "rabbit-uk-prod//orders/payments-dlq", ActionRisk.Mutating, false,
            "republish: 2 routed, 1 unroutable", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Republish_stops_at_the_first_failure_and_says_how_far_it_got()
    {
        Operations.PublishAsync(Secret, Vhost, Arg.Any<PublishRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PublishOutcome.Routed), Task.FromException<PublishOutcome>(Forbidden), Task.FromResult(PublishOutcome.Routed));

        var result = await new RepublishMessagesCommandHandler(Operations, Connections, Audit, Log<RepublishMessagesCommandHandler>())
            .HandleAsync(new(ConnectionId, Conn, Vhost, "payments-dlq", "requeue", [Message(), Message(), Message()]));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("1 of 3 were published first");
        await Operations.Received(2).PublishAsync(Secret, Vhost, Arg.Any<PublishRequest>(), Arg.Any<CancellationToken>());
        await Audit.Received(1).RecordAsync(Arg.Any<string>(), Arg.Any<string>(), ActionRisk.Mutating, false,
            Arg.Is<string>(d => d.StartsWith("requeue: 1 routed")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Consume_uses_consume_mode_and_is_audited_destructive()
    {
        Operations.GetMessagesAsync(Secret, Vhost, "payments-dlq", 5, GetMode.Consume, Arg.Any<CancellationToken>()).Returns([]);

        var result = await new ConsumeMessagesCommandHandler(Operations, Connections, Audit, Log<ConsumeMessagesCommandHandler>())
            .HandleAsync(new(ConnectionId, Conn, Vhost, "payments-dlq", 5));

        result.IsSuccess.Should().BeTrue();
        await Audit.Received(1).RecordAsync("rabbitmq.message.consume", "rabbit-uk-prod//orders/payments-dlq", ActionRisk.Destructive, true, "0 consumed", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Consume_failure_is_friendly_and_audited()
    {
        Operations.GetMessagesAsync(Secret, Vhost, "payments-dlq", 5, GetMode.Consume, Arg.Any<CancellationToken>()).ThrowsAsync(Forbidden);

        var result = await new ConsumeMessagesCommandHandler(Operations, Connections, Audit, Log<ConsumeMessagesCommandHandler>())
            .HandleAsync(new(ConnectionId, Conn, Vhost, "payments-dlq", 5));

        result.Error.Should().Be(ForbiddenText);
        await Audit.Received(1).RecordAsync("rabbitmq.message.consume", Arg.Any<string>(), ActionRisk.Destructive, false, ForbiddenText, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Every_handler_resolves_after_ConfigureServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Connections);
        services.AddSingleton(Audit);
        new RabbitMqPlugin().ConfigureServices(services);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using var scope = provider.CreateScope();

        var handlerTypes = typeof(RabbitMqPlugin).Assembly.GetTypes()
            .Where(t => t.IsPublic && (t.Name.EndsWith("QueryHandler", StringComparison.Ordinal) || t.Name.EndsWith("CommandHandler", StringComparison.Ordinal)))
            .ToList();

        handlerTypes.Should().HaveCountGreaterThan(20);
        foreach (var type in handlerTypes)
        {
            scope.ServiceProvider.GetService(type).Should().NotBeNull($"{type.Name} must be registered");
        }
    }
}
