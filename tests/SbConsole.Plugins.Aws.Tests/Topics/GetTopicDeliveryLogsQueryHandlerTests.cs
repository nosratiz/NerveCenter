using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Topics;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Topics;

public class GetTopicDeliveryLogsQueryHandlerTests
{
    private const string Secret = "mode=access-keys;region=us-east-1";
    private const string TopicArn = "arn:aws:sns:us-east-1:123456789012:orders";
    private readonly ISnsOperations _operations = Substitute.For<ISnsOperations>();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly Guid _connectionId = Guid.NewGuid();

    private GetTopicDeliveryLogsQueryHandler CreateHandler()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns(Secret);
        return new GetTopicDeliveryLogsQueryHandler(_operations, _connections, NullLogger<GetTopicDeliveryLogsQueryHandler>.Instance);
    }

    [Fact]
    public async Task Returns_the_logs_from_operations_with_the_requested_window_and_limit()
    {
        var logs = new DeliveryLogsResult([new DeliveryLogEntry(DateTimeOffset.UnixEpoch, "SUCCESS", "m-1", null, 200, null, 5, 1)], LoggingNotConfigured: false, IsTruncated: false);
        _operations.GetDeliveryLogsAsync(Secret, TopicArn, TimeSpan.FromHours(24), 50, Arg.Any<CancellationToken>()).Returns(logs);

        var result = await CreateHandler().HandleAsync(_connectionId, TopicArn, TimeSpan.FromHours(24), 50);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeSameAs(logs);
    }

    [Fact]
    public async Task A_missing_connection_fails_without_calling_operations()
    {
        var handler = CreateHandler();
        var unknownId = Guid.NewGuid();
        _connections.GetSecretAsync(unknownId, Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await handler.HandleAsync(unknownId, TopicArn, TimeSpan.FromHours(1));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Connection not found.");
        await _operations.DidNotReceiveWithAnyArgs().GetDeliveryLogsAsync(default!, default!, default, default, default);
    }

    [Fact]
    public async Task A_failed_logs_call_fails_this_handler_without_throwing_and_writes_no_audit()
    {
        _operations.GetDeliveryLogsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<DeliveryLogsResult>(new InvalidOperationException("AccessDenied")));

        var result = await CreateHandler().HandleAsync(_connectionId, TopicArn, TimeSpan.FromHours(1));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("AccessDenied");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task A_non_positive_window_or_limit_is_rejected(int value)
    {
        var handler = CreateHandler();

        (await handler.HandleAsync(_connectionId, TopicArn, TimeSpan.FromHours(value))).IsSuccess.Should().BeFalse();
        (await handler.HandleAsync(_connectionId, TopicArn, TimeSpan.FromHours(1), value)).IsSuccess.Should().BeFalse();
        await _operations.DidNotReceiveWithAnyArgs().GetDeliveryLogsAsync(default!, default!, default, default, default);
    }
}
