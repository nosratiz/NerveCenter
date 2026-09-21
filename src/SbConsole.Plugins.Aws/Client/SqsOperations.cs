using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Client;

/// <summary>
/// The only real implementation of ISqsOperations. Its own methods get light test coverage by
/// necessity -- they can't be meaningfully unit-tested without a real or emulated AWS account
/// (design spec §8) -- the substitutable interface is where the test leverage is.
/// </summary>
public sealed class SqsOperations : ISqsOperations
{
    private static AmazonSQSClient BuildSqsClient(string secret)
    {
        var parsed = AwsConfigParser.Parse(secret);
        var config = AwsCredentialsFactory.BuildConfig(parsed);
        var credentials = AwsCredentialsFactory.BuildCredentials(parsed);
        return credentials is null ? new AmazonSQSClient(config) : new AmazonSQSClient(credentials, config);
    }

    private static AmazonSecurityTokenServiceClient BuildStsClient(string secret)
    {
        var parsed = AwsConfigParser.Parse(secret);
        var sqsConfig = AwsCredentialsFactory.BuildConfig(parsed);
        var stsConfig = new AmazonSecurityTokenServiceConfig
        {
            RegionEndpoint = sqsConfig.RegionEndpoint,
            Timeout = sqsConfig.Timeout,
            MaxErrorRetry = sqsConfig.MaxErrorRetry,
            // LocalStack emulates STS too, so a custom endpoint (design spec §2) must redirect the
            // GetCallerIdentity pre-check the same way it redirects SQS calls -- otherwise a
            // connection test against an emulator falls through to real AWS STS and fails there
            // instead of succeeding against the emulator.
            ServiceURL = sqsConfig.ServiceURL,
            UseHttp = sqsConfig.UseHttp,
        };
        var credentials = AwsCredentialsFactory.BuildCredentials(parsed);
        return credentials is null ? new AmazonSecurityTokenServiceClient(stsConfig) : new AmazonSecurityTokenServiceClient(credentials, stsConfig);
    }

    public async Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default)
    {
        try
        {
            using var sts = BuildStsClient(secret);
            await sts.GetCallerIdentityAsync(new GetCallerIdentityRequest(), ct);
        }
        catch (Exception ex)
        {
            return new ConnectionTestResult(false, FriendlyAwsError.From(ex));
        }

        // Credentials are valid (GetCallerIdentity succeeded). A denied ListQueues probe is
        // reported as a non-fatal note, not a failure -- design spec §4's "text-only diagnostics"
        // decision: nothing is persisted and no button is hidden as a result.
        try
        {
            using var sqs = BuildSqsClient(secret);
            await sqs.ListQueuesAsync(new ListQueuesRequest { MaxResults = 1 }, ct);
            return new ConnectionTestResult(true);
        }
        catch (Exception ex)
        {
            return new ConnectionTestResult(true, $"Valid credentials; sqs:ListQueues denied — queue actions may fail ({FriendlyAwsError.From(ex)})");
        }
    }

    public Task<IReadOnlyList<QueueSummary>> ListQueuesAsync(string secret, string? namePrefix, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in Task 4.");

    public Task<string> CreateQueueAsync(string secret, CreateQueueRequest request, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in Task 6.");

    public Task DeleteQueueAsync(string secret, string queueUrl, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in Task 7.");

    public Task PurgeQueueAsync(string secret, string queueUrl, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in Task 8.");

    public Task<IReadOnlyList<ReceivedMessage>> ReceiveMessagesAsync(
        string secret, string queueUrl, int maxMessages, int? visibilityTimeoutSeconds, int waitTimeSeconds, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in Task 9.");

    public Task DeleteMessageAsync(string secret, string queueUrl, string receiptHandle, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in Task 10.");

    public Task ChangeMessageVisibilityAsync(string secret, string queueUrl, string receiptHandle, int visibilityTimeoutSeconds, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in Task 10.");

    public Task SendMessageAsync(string secret, string queueUrl, SendMessageRequest request, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in Task 12.");

    public Task<string> StartRedriveTaskAsync(string secret, string sourceQueueArn, string destinationQueueArn, int? maxMessagesPerSecond, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in Task 13.");
}
