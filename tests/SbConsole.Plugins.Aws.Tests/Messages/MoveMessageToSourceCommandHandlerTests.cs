using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Messages;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Messages;

public class MoveMessageToSourceCommandHandlerTests
{
    private const string Secret = "mode=default-chain;region=eu-west-1";
    private const string DlqUrl = "https://sqs.eu-west-1.amazonaws.com/123456789012/orders-dlq";
    private const string SourceUrl = "https://sqs.eu-west-1.amazonaws.com/123456789012/orders";
    private const string FifoDlqUrl = "https://sqs.eu-west-1.amazonaws.com/123456789012/orders-dlq.fifo";
    private const string FifoSourceUrl = "https://sqs.eu-west-1.amazonaws.com/123456789012/orders.fifo";

    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly ISqsOperations _operations = Substitute.For<ISqsOperations>();
    private readonly IAuditScope _audit = Substitute.For<IAuditScope>();

    public MoveMessageToSourceCommandHandlerTests() =>
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns(Secret);

    private MoveMessageToSourceCommandHandler Handler() =>
        new(_operations, _connections, _audit, NullLogger<MoveMessageToSourceCommandHandler>.Instance);

    private static ReceivedMessage Message(string? groupId = null) =>
        new("msg-1", "handle-1", "{\"orderId\":1}", 6, DateTimeOffset.UtcNow, "sender", "md5",
            new Dictionary<string, string> { ["source"] = "checkout" }, groupId);

    private MoveMessageToSourceCommand Command(ReceivedMessage message, string dlqUrl = DlqUrl, string sourceUrl = SourceUrl) =>
        new(_connectionId, "aws-dev", dlqUrl, "orders-dlq", sourceUrl, message);

    [Fact]
    public async Task Sends_body_and_attributes_to_the_source_then_deletes_from_the_DLQ_and_audits_success()
    {
        var result = await Handler().HandleAsync(Command(Message()));

        result.IsSuccess.Should().BeTrue();
        Received.InOrder(() =>
        {
            _operations.SendMessageAsync(Secret, SourceUrl, Arg.Is<SendMessageRequest>(r =>
                r.Body == "{\"orderId\":1}"
                && r.MessageAttributes!["source"] == "checkout"
                && r.MessageGroupId == null
                && r.MessageDeduplicationId == null
                && r.DelaySeconds == null), Arg.Any<CancellationToken>());
            _operations.DeleteMessageAsync(Secret, DlqUrl, "handle-1", Arg.Any<CancellationToken>());
        });
        await _audit.Received(1).RecordAsync("aws.message.move", "aws-dev/orders-dlq", ActionRisk.Mutating, true,
            "msg-1 → orders", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_FIFO_source_reuses_the_group_id_and_uses_the_original_message_id_as_dedup_id()
    {
        var result = await Handler().HandleAsync(Command(Message(groupId: "customer-42"), FifoDlqUrl, FifoSourceUrl));

        result.IsSuccess.Should().BeTrue();
        await _operations.Received(1).SendMessageAsync(Secret, FifoSourceUrl, Arg.Is<SendMessageRequest>(r =>
            r.MessageGroupId == "customer-42" && r.MessageDeduplicationId == "msg-1"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_send_deletes_nothing_and_reports_a_friendly_error()
    {
        _operations.SendMessageAsync(Secret, SourceUrl, Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new Amazon.SQS.Model.QueueDoesNotExistException("gone")));

        var result = await Handler().HandleAsync(Command(Message()));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("Queue not found");
        await _operations.DidNotReceive().DeleteMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _audit.Received(1).RecordAsync("aws.message.move", "aws-dev/orders-dlq", ActionRisk.Mutating, false,
            Arg.Is<string>(d => d.Contains("Queue not found")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_delete_after_a_successful_send_reports_the_duplicate_risk_explicitly()
    {
        _operations.DeleteMessageAsync(Secret, DlqUrl, "handle-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new Amazon.SQS.Model.ReceiptHandleIsInvalidException("expired")));

        var result = await Handler().HandleAsync(Command(Message()));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("copied to orders").And.Contain("still in the DLQ").And.Contain("duplicate");
        result.Error.Should().Contain("This message's hold already expired");
        await _operations.Received(1).SendMessageAsync(Secret, SourceUrl, Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>());
        await _audit.Received(1).RecordAsync("aws.message.move", "aws-dev/orders-dlq", ActionRisk.Mutating, false,
            Arg.Is<string>(d => d.Contains("still in the DLQ")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unknown_connection_fails_without_touching_AWS()
    {
        var unknown = Guid.NewGuid();
        _connections.GetSecretAsync(unknown, Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await Handler().HandleAsync(Command(Message()) with { ConnectionId = unknown });

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Connection not found.");
        await _operations.DidNotReceive().SendMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("https://sqs.eu-west-1.amazonaws.com/123456789012/orders", "orders")]
    [InlineData("https://sqs.eu-west-1.amazonaws.com/123456789012/orders.fifo", "orders.fifo")]
    [InlineData("https://sqs.eu-west-1.amazonaws.com/123456789012/orders/", "orders")]
    public void QueueNameFromUrl_takes_the_last_path_segment(string url, string expected) =>
        MoveMessageToSourceCommandHandler.QueueNameFromUrl(url).Should().Be(expected);

    [Fact]
    public void ResolveSources_is_unavailable_when_the_detail_or_its_source_lookup_failed()
    {
        MoveMessageToSourceCommandHandler.ResolveSources(null).Should().BeNull();
        MoveMessageToSourceCommandHandler.ResolveSources(Detail(sources: null)).Should().BeNull();
    }

    [Fact]
    public void ResolveSources_returns_the_dead_letter_source_urls_distinct_and_sorted_by_name()
    {
        var sources = MoveMessageToSourceCommandHandler.ResolveSources(Detail(sources: [SourceUrl + "-b", SourceUrl, SourceUrl]));

        sources.Should().Equal(SourceUrl, SourceUrl + "-b");
    }

    [Fact]
    public void ResolveSources_is_empty_for_a_queue_nothing_dead_letters_into()
    {
        MoveMessageToSourceCommandHandler.ResolveSources(Detail(sources: [])).Should().BeEmpty();
    }

    private static QueueDetails Detail(IReadOnlyList<string>? sources) =>
        new("orders-dlq", DlqUrl, "arn:aws:sqs:eu-west-1:123456789012:orders-dlq", false, false, 1, 0, 0, null, null,
            new Dictionary<string, string>(), null, null, sources);
}
