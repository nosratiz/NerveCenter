using Amazon.SQS.Model;
using FluentAssertions;
using SbConsole.Plugins.Aws.Client;

namespace SbConsole.Plugins.Aws.Tests.Client;

public class SqsOperationsTests
{
    private static Dictionary<string, string> FullAttributes() => new()
    {
        ["QueueArn"] = "arn:aws:sqs:eu-west-1:123456789012:orders",
        ["ApproximateNumberOfMessages"] = "1204",
        ["ApproximateNumberOfMessagesNotVisible"] = "18",
        ["ApproximateNumberOfMessagesDelayed"] = "0",
        ["CreatedTimestamp"] = "1700000000",
        ["RedrivePolicy"] = "{\"deadLetterTargetArn\":\"arn:aws:sqs:eu-west-1:123456789012:orders-dlq\",\"maxReceiveCount\":5}",
        ["KmsMasterKeyId"] = "alias/aws/sqs",
        ["FifoQueue"] = "false",
    };

    [Fact]
    public void ToQueueSummary_maps_every_attribute()
    {
        var summary = SqsOperations.ToQueueSummary("orders", "https://sqs.eu-west-1.amazonaws.com/123456789012/orders", FullAttributes());

        summary.Name.Should().Be("orders");
        summary.QueueArn.Should().Be("arn:aws:sqs:eu-west-1:123456789012:orders");
        summary.IsFifo.Should().BeFalse();
        summary.ApproxVisible.Should().Be(1204);
        summary.ApproxInFlight.Should().Be(18);
        summary.ApproxDelayed.Should().Be(0);
        summary.HasDeadLetterTarget.Should().BeTrue();
        summary.IsKmsEncrypted.Should().BeTrue();
        summary.CreatedAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1700000000));
    }

    [Fact]
    public void ToQueueSummary_defaults_missing_optional_attributes()
    {
        var attributes = new Dictionary<string, string>
        {
            ["QueueArn"] = "arn:aws:sqs:eu-west-1:123456789012:plain",
            ["ApproximateNumberOfMessages"] = "0",
            ["ApproximateNumberOfMessagesNotVisible"] = "0",
            ["ApproximateNumberOfMessagesDelayed"] = "0",
            ["CreatedTimestamp"] = "1700000000",
        };

        var summary = SqsOperations.ToQueueSummary("plain", "https://sqs.eu-west-1.amazonaws.com/123456789012/plain", attributes);

        summary.HasDeadLetterTarget.Should().BeFalse();
        summary.IsKmsEncrypted.Should().BeFalse();
        summary.IsFifo.Should().BeFalse();
    }

    [Fact]
    public void ToQueueSummary_recognizes_a_FIFO_queue()
    {
        var attributes = FullAttributes();
        attributes["FifoQueue"] = "true";

        SqsOperations.ToQueueSummary("orders.fifo", "https://sqs.eu-west-1.amazonaws.com/123456789012/orders.fifo", attributes)
            .IsFifo.Should().BeTrue();
    }

    [Fact]
    public void QueueNameFromUrl_extracts_the_last_path_segment()
    {
        SqsOperations.QueueNameFromUrl("https://sqs.eu-west-1.amazonaws.com/123456789012/order-events").Should().Be("order-events");
    }

    [Fact]
    public void ToReceivedMessage_maps_system_attributes_and_custom_attributes()
    {
        var message = new Amazon.SQS.Model.Message
        {
            MessageId = "msg-1",
            ReceiptHandle = "handle-1",
            Body = "{\"orderId\":\"1001\"}",
            MD5OfBody = "abc123",
            Attributes = new Dictionary<string, string>
            {
                ["ApproximateReceiveCount"] = "3",
                ["SentTimestamp"] = "1700000000000",
                ["SenderId"] = "AIDAEXAMPLE",
            },
            MessageAttributes = new Dictionary<string, Amazon.SQS.Model.MessageAttributeValue>
            {
                ["source"] = new() { StringValue = "checkout", DataType = "String" },
            },
        };

        var received = SqsOperations.ToReceivedMessage(message);

        received.MessageId.Should().Be("msg-1");
        received.ReceiptHandle.Should().Be("handle-1");
        received.ApproxReceiveCount.Should().Be(3);
        received.SentTimestamp.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1700000000000));
        received.SenderId.Should().Be("AIDAEXAMPLE");
        received.Md5OfBody.Should().Be("abc123");
        received.MessageAttributes.Should().Equal(new Dictionary<string, string> { ["source"] = "checkout" });
    }
}
