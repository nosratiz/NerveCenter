using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Redrive;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Redrive;

public class CancelRedriveCommandHandlerTests
{
    private const string Secret = "mode=default-chain;region=eu-west-1";
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly ISqsOperations _operations = Substitute.For<ISqsOperations>();
    private readonly IAuditScope _audit = Substitute.For<IAuditScope>();

    public CancelRedriveCommandHandlerTests()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns(Secret);
    }

    private CancelRedriveCommandHandler Handler() =>
        new(_operations, _connections, _audit, NullLogger<CancelRedriveCommandHandler>.Instance);

    [Fact]
    public async Task Cancels_the_move_task_and_records_a_Mutating_audit_entry()
    {
        var result = await Handler().HandleAsync(new CancelRedriveCommand(_connectionId, "aws-dev", "handle-1", "orders-dlq"));

        result.IsSuccess.Should().BeTrue();
        await _operations.Received(1).CancelMessageMoveTaskAsync(Secret, "handle-1", Arg.Any<CancellationToken>());
        await _audit.Received(1).RecordAsync("aws.queue.redrive.cancel", "aws-dev/orders-dlq", ActionRisk.Mutating, true, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Operation_failure_records_a_failed_audit_entry()
    {
        _operations.CancelMessageMoveTaskAsync(Secret, "handle-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("the task is no longer running")));

        var result = await Handler().HandleAsync(new CancelRedriveCommand(_connectionId, "aws-dev", "handle-1", "orders-dlq"));

        result.IsSuccess.Should().BeFalse();
        await _audit.Received(1).RecordAsync("aws.queue.redrive.cancel", "aws-dev/orders-dlq", ActionRisk.Mutating, false, "the task is no longer running", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unknown_connection_fails_without_calling_operations()
    {
        var unknownId = Guid.NewGuid();
        _connections.GetSecretAsync(unknownId, Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await Handler().HandleAsync(new CancelRedriveCommand(unknownId, "aws-dev", "handle-1", "orders-dlq"));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Connection not found.");
        await _operations.DidNotReceive().CancelMessageMoveTaskAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
