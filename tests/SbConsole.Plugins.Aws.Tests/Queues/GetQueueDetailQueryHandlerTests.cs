using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Queues;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Queues;

public class GetQueueDetailQueryHandlerTests
{
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly ISqsOperations _operations = Substitute.For<ISqsOperations>();

    public GetQueueDetailQueryHandlerTests()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns("mode=default-chain;region=eu-west-1");
    }

    private GetQueueDetailQueryHandler Handler() =>
        new(_operations, _connections, NullLogger<GetQueueDetailQueryHandler>.Instance);

    [Fact]
    public async Task Returns_the_queue_detail_from_operations()
    {
        var detail = SqsOperations.ToQueueDetail("https://sqs/orders", new Dictionary<string, string>(), null, null);
        _operations.GetQueueDetailAsync("mode=default-chain;region=eu-west-1", "https://sqs/orders", Arg.Any<CancellationToken>())
            .Returns(detail);

        var result = await Handler().HandleAsync(_connectionId, "https://sqs/orders");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeSameAs(detail);
    }

    [Fact]
    public async Task Unknown_connection_fails_without_calling_operations()
    {
        var unknownId = Guid.NewGuid();
        _connections.GetSecretAsync(unknownId, Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await Handler().HandleAsync(unknownId, "https://sqs/orders");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Connection not found.");
        await _operations.DidNotReceive().GetQueueDetailAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Operation_failure_is_reduced_to_a_friendly_error()
    {
        _operations.GetQueueDetailAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<QueueDetails>(new InvalidOperationException("boom")));

        var result = await Handler().HandleAsync(_connectionId, "https://sqs/orders");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace();
    }
}
