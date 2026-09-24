using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Redrive;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Redrive;

public class ListRedriveTasksQueryHandlerTests
{
    private const string Secret = "mode=default-chain;region=eu-west-1";
    private const string SourceArn = "arn:aws:sqs:eu-west-1:123456789012:orders-dlq";
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly ISqsOperations _operations = Substitute.For<ISqsOperations>();
    private readonly IAuditScope _audit = Substitute.For<IAuditScope>();

    public ListRedriveTasksQueryHandlerTests()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns(Secret);
    }

    private ListRedriveTasksQueryHandler Handler() =>
        new(_operations, _connections, NullLogger<ListRedriveTasksQueryHandler>.Instance);

    [Fact]
    public async Task Returns_the_move_tasks_for_the_source_queue()
    {
        var tasks = new List<MessageMoveTaskSummary>
        {
            new("handle-1", "RUNNING", SourceArn, "arn:aws:sqs:eu-west-1:123456789012:orders", 10, 100, null, DateTimeOffset.UnixEpoch),
        };
        _operations.ListMessageMoveTasksAsync(Secret, SourceArn, Arg.Any<CancellationToken>()).Returns(tasks);

        var result = await Handler().HandleAsync(_connectionId, SourceArn);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(tasks);
    }

    [Fact]
    public async Task Unknown_connection_fails_without_calling_operations()
    {
        var unknownId = Guid.NewGuid();
        _connections.GetSecretAsync(unknownId, Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await Handler().HandleAsync(unknownId, SourceArn);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Connection not found.");
        await _operations.DidNotReceive().ListMessageMoveTasksAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Operation_failure_is_reduced_to_a_friendly_error_and_never_audited()
    {
        _operations.ListMessageMoveTasksAsync(Secret, SourceArn, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<MessageMoveTaskSummary>>(new InvalidOperationException("boom")));

        var result = await Handler().HandleAsync(_connectionId, SourceArn);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace();
        _audit.ReceivedCalls().Should().BeEmpty();
    }
}
