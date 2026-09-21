using Amazon.SecurityToken;
using Amazon.SQS;
using Amazon.SQS.Model;
using FluentAssertions;
using SbConsole.Plugins.Aws.Client;

namespace SbConsole.Plugins.Aws.Tests.Client;

public class FriendlyAwsErrorTests
{
    [Fact]
    public void QueueDoesNotExist_maps_to_a_fixed_message()
    {
        var ex = new QueueDoesNotExistException("raw SQS text");

        FriendlyAwsError.From(ex).Should().Be("Queue not found");
    }

    [Fact]
    public void QueueNameExists_maps_to_a_fixed_conflict_message()
    {
        var ex = new QueueNameExistsException("raw SQS text");

        FriendlyAwsError.From(ex).Should().Be("A queue with this name already exists with different settings");
    }

    [Fact]
    public void ReceiptHandleIsInvalid_maps_to_a_fixed_message()
    {
        var ex = new ReceiptHandleIsInvalidException("raw SQS text");

        FriendlyAwsError.From(ex).Should().Be("This message's hold already expired — it's back in the queue");
    }

    [Fact]
    public void RequestThrottled_maps_to_a_fixed_message()
    {
        var ex = new RequestThrottledException("raw SQS text");

        FriendlyAwsError.From(ex).Should().Be("AWS is throttling this connection — try again shortly");
    }

    [Fact]
    public void InvalidClientTokenId_maps_to_a_fixed_message()
    {
        var ex = new AmazonSecurityTokenServiceException("raw STS text") { ErrorCode = "InvalidClientTokenId" };

        FriendlyAwsError.From(ex).Should().Be("Credentials rejected");
    }

    [Fact]
    public void AccessDenied_on_STS_maps_to_a_fixed_message()
    {
        var ex = new AmazonSecurityTokenServiceException("raw text") { ErrorCode = "AccessDenied" };

        FriendlyAwsError.From(ex).Should().Be("Access denied — check IAM permissions");
    }

    [Fact]
    public void AccessDenied_on_SQS_maps_to_a_fixed_message()
    {
        var ex = new AmazonSQSException("raw text") { ErrorCode = "AccessDenied" };

        FriendlyAwsError.From(ex).Should().Be("Access denied — check IAM permissions");
    }

    [Fact]
    public void Non_AWS_exceptions_fall_back_to_the_shared_FriendlyError_helper()
    {
        var ex = new InvalidOperationException("plain failure");

        FriendlyAwsError.From(ex).Should().Be("plain failure");
    }
}
