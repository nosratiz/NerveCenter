using Amazon.CloudWatch;
using Amazon.CloudWatchLogs;
using Amazon.SecurityToken;
using Amazon.SimpleNotificationService;
using Amazon.SQS;
using Amazon.SQS.Model;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Client;

/// <summary>
/// AWS-specific counterpart to SbConsole.Sdk.FriendlyError: maps the exception types this
/// plugin's operations can actually hit to a short, fixed, readable message, falling back to
/// the (capped/collapsed) exception text for anything unmapped. Mirrors
/// SbConsole.Plugins.Kafka.Client.FriendlyKafkaError's shape. Exception types confirmed against
/// the installed AWSSDK.SQS/AWSSDK.SecurityToken package versions at implementation time, same
/// "confirmed, not assumed" bar the Kafka/Service Bus plugins' error mapping holds itself to.
/// </summary>
public static class FriendlyAwsError
{
    public static string From(Exception ex) => ex switch
    {
        AmazonSecurityTokenServiceException { ErrorCode: "InvalidClientTokenId" } => "Credentials rejected",
        AmazonSecurityTokenServiceException { ErrorCode: "UnrecognizedClientException" } => "Credentials rejected",
        AmazonSecurityTokenServiceException { ErrorCode: "AccessDenied" } => "Access denied — check IAM permissions",
        AmazonSQSException { ErrorCode: "AccessDenied" } => "Access denied — check IAM permissions",
        QueueDoesNotExistException => "Queue not found",
        QueueNameExistsException => "A queue with this name already exists with different settings",
        ReceiptHandleIsInvalidException => "This message's hold already expired — it's back in the queue",
        RequestThrottledException => "AWS is throttling this connection — try again shortly",
        AmazonSimpleNotificationServiceException { ErrorCode: "AuthorizationError" } => "Access denied — check IAM permissions",
        AmazonSimpleNotificationServiceException { ErrorCode: "NotFound" } => "Topic or subscription not found",
        AmazonCloudWatchException { ErrorCode: "AccessDenied" } => "Access denied — check IAM permissions for cloudwatch:GetMetricStatistics",
        // FilterLogEvents is the only CloudWatch Logs call this plugin makes. ResourceNotFound (no log
        // group) never gets here -- SnsDeliveryLogs.ScanAsync turns it into "logging not configured".
        Amazon.CloudWatchLogs.Model.AccessDeniedException => "Access denied — check IAM permissions for logs:FilterLogEvents",
        AmazonCloudWatchLogsException { ErrorCode: "AccessDeniedException" } => "Access denied — check IAM permissions for logs:FilterLogEvents",
        _ => FriendlyError.From(ex),
    };
}
