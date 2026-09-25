using Amazon.CloudWatch;

namespace SbConsole.Plugins.Aws.Client;

/// <summary>
/// Builds an AmazonCloudWatchClient from the connection secret, shared by SnsOperations (topic
/// delivery-failure count) and SqsOperations (oldest-message age). Same construction pattern as the
/// plugin's other clients: a fresh client per call, with region/timeout/retry and ServiceURL/UseHttp
/// forwarded from the shared SQS config so a custom LocalStack endpoint redirects CloudWatch too.
/// </summary>
internal static class CloudWatchClientFactory
{
    internal static AmazonCloudWatchClient Build(string secret)
    {
        var parsed = AwsConfigParser.Parse(secret);
        var sqsConfig = AwsCredentialsFactory.BuildConfig(parsed);
        var cwConfig = new AmazonCloudWatchConfig
        {
            RegionEndpoint = sqsConfig.RegionEndpoint,
            Timeout = sqsConfig.Timeout,
            MaxErrorRetry = sqsConfig.MaxErrorRetry,
            ServiceURL = sqsConfig.ServiceURL,
            UseHttp = sqsConfig.UseHttp,
        };
        var credentials = AwsCredentialsFactory.BuildCredentials(parsed);
        return credentials is null ? new AmazonCloudWatchClient(cwConfig) : new AmazonCloudWatchClient(credentials, cwConfig);
    }
}
