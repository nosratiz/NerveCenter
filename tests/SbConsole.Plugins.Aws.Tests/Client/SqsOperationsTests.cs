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
        var summary = SqsOperations.ToQueueSummary("orders", "https://sqs.eu-west-1.amazonaws.com/123456789012/orders", FullAttributes(), deadLetterSourceCount: 0);

        summary.Name.Should().Be("orders");
        summary.QueueArn.Should().Be("arn:aws:sqs:eu-west-1:123456789012:orders");
        summary.IsFifo.Should().BeFalse();
        summary.ApproxVisible.Should().Be(1204);
        summary.ApproxInFlight.Should().Be(18);
        summary.ApproxDelayed.Should().Be(0);
        // "orders" has its own RedrivePolicy (it sends failed messages elsewhere) -- that makes it
        // a source queue, not a DLQ, so HasDeadLetterTarget stays true (harmless, kept for a future
        // "redrive-out" panel) while DeadLetterSourceCount -- the field the UI actually keys off of
        // -- reflects how many *other* queues redrive into "orders" (zero here; see the
        // redrive-target test below).
        summary.HasDeadLetterTarget.Should().BeTrue();
        summary.DeadLetterSourceCount.Should().Be(0);
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

        var summary = SqsOperations.ToQueueSummary("plain", "https://sqs.eu-west-1.amazonaws.com/123456789012/plain", attributes, deadLetterSourceCount: 0);

        summary.HasDeadLetterTarget.Should().BeFalse();
        summary.DeadLetterSourceCount.Should().Be(0);
        summary.IsKmsEncrypted.Should().BeFalse();
        summary.IsFifo.Should().BeFalse();
    }

    [Fact]
    public void ToQueueSummary_recognizes_a_FIFO_queue()
    {
        var attributes = FullAttributes();
        attributes["FifoQueue"] = "true";

        SqsOperations.ToQueueSummary("orders.fifo", "https://sqs.eu-west-1.amazonaws.com/123456789012/orders.fifo", attributes, deadLetterSourceCount: 0)
            .IsFifo.Should().BeTrue();
    }

    [Fact]
    public void ToQueueSummary_reports_the_actual_dead_letter_source_count_passed_in()
    {
        // "orders-dlq" has no RedrivePolicy of its own (it IS the dead-letter target, not a
        // source), but two other queues' RedrivePolicy point at it -- this is the actual DLQ
        // signal the UI's chip/Redrive button should key off of, computed by the caller
        // (ListQueuesAsync) and passed straight through here.
        var attributes = new Dictionary<string, string>
        {
            ["QueueArn"] = "arn:aws:sqs:eu-west-1:123456789012:orders-dlq",
            ["ApproximateNumberOfMessages"] = "3",
            ["ApproximateNumberOfMessagesNotVisible"] = "0",
            ["ApproximateNumberOfMessagesDelayed"] = "0",
            ["CreatedTimestamp"] = "1700000000",
        };

        var summary = SqsOperations.ToQueueSummary("orders-dlq", "https://sqs.eu-west-1.amazonaws.com/123456789012/orders-dlq", attributes, deadLetterSourceCount: 2);

        summary.HasDeadLetterTarget.Should().BeFalse();
        summary.DeadLetterSourceCount.Should().Be(2);
    }

    [Fact]
    public void QueueNameFromUrl_extracts_the_last_path_segment()
    {
        SqsOperations.QueueNameFromUrl("https://sqs.eu-west-1.amazonaws.com/123456789012/order-events").Should().Be("order-events");
    }

    [Fact]
    public void ExtractDeadLetterTargetArn_parses_the_target_arn_out_of_a_well_formed_RedrivePolicy()
    {
        var json = "{\"deadLetterTargetArn\":\"arn:aws:sqs:eu-west-1:123456789012:orders-dlq\",\"maxReceiveCount\":5}";

        SqsOperations.ExtractDeadLetterTargetArn(json).Should().Be("arn:aws:sqs:eu-west-1:123456789012:orders-dlq");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"maxReceiveCount\":5}")]
    public void ExtractDeadLetterTargetArn_returns_null_for_missing_or_malformed_input(string? redrivePolicyJson)
    {
        SqsOperations.ExtractDeadLetterTargetArn(redrivePolicyJson).Should().BeNull();
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
        received.MessageAttributes.Should().ContainSingle();
        received.MessageAttributes["source"].Should().BeEquivalentTo(new SqsMessageAttribute("String", "checkout", null));
        received.MessageGroupId.Should().BeNull("a standard-queue message carries no MessageGroupId attribute");
    }

    [Fact]
    public void ToReceivedMessage_maps_the_FIFO_MessageGroupId_system_attribute()
    {
        var message = new Amazon.SQS.Model.Message
        {
            MessageId = "msg-1",
            ReceiptHandle = "handle-1",
            Body = "{}",
            MD5OfBody = "abc123",
            Attributes = new Dictionary<string, string> { ["MessageGroupId"] = "customer-42" },
            MessageAttributes = new Dictionary<string, Amazon.SQS.Model.MessageAttributeValue>(),
        };

        SqsOperations.ToReceivedMessage(message).MessageGroupId.Should().Be("customer-42");
    }
    [Fact]
    public void ParseRedrivePolicy_parses_target_arn_and_numeric_maxReceiveCount()
    {
        var policy = SqsOperations.ParseRedrivePolicy(
            "{\"deadLetterTargetArn\":\"arn:aws:sqs:eu-west-1:123456789012:orders-dlq\",\"maxReceiveCount\":5}");

        policy.Should().NotBeNull();
        policy!.DeadLetterTargetArn.Should().Be("arn:aws:sqs:eu-west-1:123456789012:orders-dlq");
        policy.DeadLetterTargetName.Should().Be("orders-dlq");
        policy.MaxReceiveCount.Should().Be(5);
    }

    [Fact]
    public void ParseRedrivePolicy_accepts_a_string_maxReceiveCount()
    {
        // AWS has historically returned maxReceiveCount as a JSON string ("5") on some queues.
        var policy = SqsOperations.ParseRedrivePolicy(
            "{\"deadLetterTargetArn\":\"arn:aws:sqs:eu-west-1:1:orders-dlq\",\"maxReceiveCount\":\"7\"}");

        policy!.MaxReceiveCount.Should().Be(7);
    }

    [Fact]
    public void ParseRedrivePolicy_leaves_maxReceiveCount_null_when_missing()
    {
        var policy = SqsOperations.ParseRedrivePolicy("{\"deadLetterTargetArn\":\"arn:aws:sqs:eu-west-1:1:orders-dlq\"}");

        policy!.MaxReceiveCount.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"maxReceiveCount\":5}")]
    public void ParseRedrivePolicy_returns_null_for_missing_or_malformed_input(string? json)
    {
        SqsOperations.ParseRedrivePolicy(json).Should().BeNull();
    }

    [Fact]
    public void ToQueueDetail_maps_attributes_tags_redrive_policy_and_sources()
    {
        var attributes = FullAttributes();
        attributes["ApproximateNumberOfMessagesDelayed"] = "3";
        attributes["LastModifiedTimestamp"] = "1700000500";
        var tags = new Dictionary<string, string> { ["team"] = "payments" };

        var detail = SqsOperations.ToQueueDetail(
            "https://sqs.eu-west-1.amazonaws.com/123456789012/orders", attributes, tags, ["https://sqs/orders-src"]);

        detail.Name.Should().Be("orders");
        detail.QueueArn.Should().Be("arn:aws:sqs:eu-west-1:123456789012:orders");
        detail.IsFifo.Should().BeFalse();
        detail.IsKmsEncrypted.Should().BeTrue();
        detail.ApproxVisible.Should().Be(1204);
        detail.ApproxInFlight.Should().Be(18);
        detail.ApproxDelayed.Should().Be(3);
        detail.CreatedAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1700000000));
        detail.LastModifiedAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1700000500));
        detail.Attributes.Should().ContainKey("QueueArn");
        detail.Tags.Should().Equal(tags);
        detail.RedrivePolicy!.DeadLetterTargetName.Should().Be("orders-dlq");
        detail.RedrivePolicy.MaxReceiveCount.Should().Be(5);
        detail.DeadLetterSourceQueueUrls.Should().Equal("https://sqs/orders-src");
        detail.IsDeadLetterQueue.Should().BeTrue();
    }

    [Fact]
    public void ToQueueDetail_keeps_unavailable_tags_and_sources_as_null_and_missing_timestamps_as_null()
    {
        var detail = SqsOperations.ToQueueDetail(
            "https://sqs/orders.fifo", new Dictionary<string, string> { ["FifoQueue"] = "true" }, tags: null, deadLetterSourceQueueUrls: null);

        detail.IsFifo.Should().BeTrue();
        detail.CreatedAt.Should().BeNull();
        detail.LastModifiedAt.Should().BeNull();
        detail.Tags.Should().BeNull();
        detail.DeadLetterSourceQueueUrls.Should().BeNull();
        detail.IsDeadLetterQueue.Should().BeFalse();
        detail.RedrivePolicy.Should().BeNull();
    }

    [Fact]
    public void ToMessageMoveTaskSummary_maps_a_running_task_with_epoch_millis_start()
    {
        var entry = new ListMessageMoveTasksResultEntry
        {
            TaskHandle = "handle-1",
            Status = "RUNNING",
            SourceArn = "arn:aws:sqs:eu-west-1:123456789012:orders-dlq",
            DestinationArn = "arn:aws:sqs:eu-west-1:123456789012:orders",
            ApproximateNumberOfMessagesMoved = 25,
            ApproximateNumberOfMessagesToMove = 100,
            StartedTimestamp = 1700000000123,
        };

        var task = SqsOperations.ToMessageMoveTaskSummary(entry);

        task.TaskHandle.Should().Be("handle-1");
        task.Status.Should().Be("RUNNING");
        task.IsRunning.Should().BeTrue();
        task.SourceArn.Should().Be("arn:aws:sqs:eu-west-1:123456789012:orders-dlq");
        task.DestinationArn.Should().Be("arn:aws:sqs:eu-west-1:123456789012:orders");
        task.MessagesMoved.Should().Be(25);
        task.MessagesToMove.Should().Be(100);
        task.ProgressPercent.Should().Be(25);
        task.FailureReason.Should().BeNull();
        task.StartedAt.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1700000000123));
    }

    [Fact]
    public void ToMessageMoveTaskSummary_treats_unset_fields_as_unknown()
    {
        // AWSSDK.SQS 3.7 exposes these as non-nullable longs/strings, so an absent value arrives as
        // 0/null: TaskHandle is only returned for RUNNING tasks, and ToMove only while running.
        var entry = new ListMessageMoveTasksResultEntry
        {
            Status = "FAILED",
            SourceArn = "arn:aws:sqs:eu-west-1:123456789012:orders-dlq",
            FailureReason = "AWS.SimpleQueueService.NonExistentQueue",
        };

        var task = SqsOperations.ToMessageMoveTaskSummary(entry);

        task.TaskHandle.Should().BeNull();
        task.IsRunning.Should().BeFalse();
        task.DestinationArn.Should().BeNull();
        task.MessagesToMove.Should().BeNull();
        task.ProgressPercent.Should().BeNull();
        task.StartedAt.Should().BeNull();
        task.FailureReason.Should().Be("AWS.SimpleQueueService.NonExistentQueue");
    }

    [Theory]
    [InlineData("RUNNING", true, true)]
    [InlineData("CANCELLING", false, true)]
    [InlineData("COMPLETED", false, false)]
    [InlineData("CANCELLED", false, false)]
    [InlineData("FAILED", false, false)]
    public void A_move_task_is_active_while_running_or_cancelling_but_only_cancellable_while_running(string status, bool running, bool active)
    {
        var task = new MessageMoveTaskSummary("h", status, "arn:src", null, 0, null, null, null);

        task.IsRunning.Should().Be(running);
        task.IsActive.Should().Be(active);
    }

    [Fact]
    public void ToReceivedMessage_keeps_each_attribute_s_data_type_and_binary_value()
    {
        var message = new Amazon.SQS.Model.Message
        {
            MessageId = "msg-1",
            ReceiptHandle = "handle-1",
            Body = "{}",
            MD5OfBody = "abc123",
            Attributes = new Dictionary<string, string>(),
            MessageAttributes = new Dictionary<string, Amazon.SQS.Model.MessageAttributeValue>
            {
                ["count"] = new() { DataType = "Number", StringValue = "3" },
                ["price"] = new() { DataType = "Number.float", StringValue = "9.99" },
                ["blob"] = new() { DataType = "Binary", BinaryValue = new MemoryStream([1, 2, 3]) },
            },
        };

        var attributes = SqsOperations.ToReceivedMessage(message).MessageAttributes;

        attributes["count"].Should().BeEquivalentTo(new SqsMessageAttribute("Number", "3", null));
        attributes["price"].Should().BeEquivalentTo(new SqsMessageAttribute("Number.float", "9.99", null));
        attributes["blob"].DataType.Should().Be("Binary");
        attributes["blob"].StringValue.Should().BeNull();
        attributes["blob"].BinaryValue.Should().Equal(1, 2, 3);
        attributes["blob"].DisplayValue.Should().Be("(binary, 3 bytes)");
        attributes["count"].DisplayValue.Should().Be("3");
    }

    [Fact]
    public void ToSdkMessageAttributes_sends_plain_string_attributes_as_String()
    {
        var request = new SbConsole.Plugins.Aws.Client.SendMessageRequest("body", new Dictionary<string, string> { ["source"] = "checkout" }, null, null, null);

        var sdk = SqsOperations.ToSdkMessageAttributes(request);

        sdk.Should().ContainSingle();
        sdk!["source"].DataType.Should().Be("String");
        sdk["source"].StringValue.Should().Be("checkout");
    }

    [Fact]
    public void ToSdkMessageAttributes_passes_typed_attributes_through_unchanged()
    {
        var typed = new Dictionary<string, SqsMessageAttribute>
        {
            ["count"] = new("Number", "3", null),
            ["blob"] = new("Binary", null, [1, 2, 3]),
            ["tag"] = new("String.custom", "x", null),
        };
        var request = new SbConsole.Plugins.Aws.Client.SendMessageRequest("body", null, null, null, null, typed);

        var sdk = SqsOperations.ToSdkMessageAttributes(request)!;

        sdk["count"].DataType.Should().Be("Number");
        sdk["count"].StringValue.Should().Be("3");
        sdk["blob"].DataType.Should().Be("Binary");
        sdk["blob"].StringValue.Should().BeNull();
        sdk["blob"].BinaryValue.ToArray().Should().Equal(1, 2, 3);
        sdk["tag"].DataType.Should().Be("String.custom");
    }

    [Fact]
    public void ToSdkMessageAttributes_is_null_when_there_are_none()
    {
        SqsOperations.ToSdkMessageAttributes(new SbConsole.Plugins.Aws.Client.SendMessageRequest("body", null, null, null, null)).Should().BeNull();
    }
}
