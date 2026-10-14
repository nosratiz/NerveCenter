using Amazon.CloudWatchLogs;

namespace SbConsole.Plugins.Aws.Client;

/// <summary>
/// Builds an AmazonCloudWatchLogsClient from the connection secret for SNS delivery-status logs --
/// same construction as CloudWatchClientFactory: a fresh client per call, with region/timeout/retry
/// and ServiceURL/UseHttp forwarded from the shared SQS config so a custom LocalStack endpoint
/// redirects CloudWatch Logs too.
/// </summary>
internal static class CloudWatchLogsClientFactory
{
    internal static AmazonCloudWatchLogsClient Build(string secret)
    {
        var parsed = AwsConfigParser.Parse(secret);
        var sqsConfig = AwsCredentialsFactory.BuildConfig(parsed);
        var logsConfig = new AmazonCloudWatchLogsConfig
        {
            RegionEndpoint = sqsConfig.RegionEndpoint,
            Timeout = sqsConfig.Timeout,
            MaxErrorRetry = sqsConfig.MaxErrorRetry,
            ServiceURL = sqsConfig.ServiceURL,
            UseHttp = sqsConfig.UseHttp,
        };
        var credentials = AwsCredentialsFactory.BuildCredentials(parsed);
        return credentials is null ? new AmazonCloudWatchLogsClient(logsConfig) : new AmazonCloudWatchLogsClient(credentials, logsConfig);
    }
}
