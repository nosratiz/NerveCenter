using Amazon;
using Amazon.Runtime;
using Amazon.SecurityToken;
using Amazon.SQS;

namespace SbConsole.Plugins.Aws.Client;

/// <summary>
/// Builds AWSCredentials/AmazonSQSConfig from a parsed AwsConfigParser dictionary -- see the
/// design spec §2. null from BuildCredentials means "construct the client with no explicit
/// credentials" (mode=default-chain): the AWS SDK resolves its own default chain (environment
/// variables, shared config/credentials file, container/instance metadata) internally when none
/// is supplied, so this plugin never constructs an AWSCredentials object for that mode at all.
/// </summary>
public static class AwsCredentialsFactory
{
    public static AmazonSQSConfig BuildConfig(IReadOnlyDictionary<string, string> parsed)
    {
        var config = new AmazonSQSConfig();
        if (parsed.TryGetValue("region", out var region))
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(region);
        }

        if (parsed.TryGetValue("endpoint", out var endpoint))
        {
            config.ServiceURL = endpoint;
            // "Path-style addressing" (design spec §2) maps to AmazonSQSConfig.UseHttp for a
            // plain-HTTP emulator endpoint like LocalStack -- confirmed against the installed
            // AWSSDK.SQS version at implementation time; SQS's endpoint construction differs from
            // S3's virtual-hosted-vs-path-style distinction the mockup's label is borrowed from.
            if (parsed.TryGetValue("pathStyle", out var pathStyle) && bool.TryParse(pathStyle, out var isPathStyle))
            {
                config.UseHttp = isPathStyle;
            }
        }

        // Tightened per design spec §7 so an unreachable region or a bad custom endpoint fails
        // fast with a clear message instead of hanging the page.
        config.Timeout = TimeSpan.FromSeconds(10);
        config.MaxErrorRetry = 2;

        return config;
    }

    public static AWSCredentials? BuildCredentials(IReadOnlyDictionary<string, string> parsed)
    {
        var mode = parsed.GetValueOrDefault("mode");
        return mode switch
        {
            "access-keys" => BuildAccessKeyCredentials(parsed),
            "assume-role" => BuildAssumeRoleCredentials(parsed),
            _ => null, // "default-chain" (or an unrecognized mode -- let the SDK's own resolution surface a clear auth error)
        };
    }

    private static AWSCredentials BuildAccessKeyCredentials(IReadOnlyDictionary<string, string> parsed)
    {
        var accessKeyId = parsed.GetValueOrDefault("accessKeyId", "");
        var secretAccessKey = parsed.GetValueOrDefault("secretAccessKey", "");
        return parsed.TryGetValue("sessionToken", out var sessionToken) && sessionToken.Length > 0
            ? new SessionAWSCredentials(accessKeyId, secretAccessKey, sessionToken)
            : new BasicAWSCredentials(accessKeyId, secretAccessKey);
    }

    // Base credentials for the sts:AssumeRole call come from the default chain (design spec §2's
    // "base credentials... come from the default chain" note) -- FallbackCredentialsFactory is
    // AWSSDK.Core's own default-chain resolver. Confirmed against the installed
    // AWSSDK.SecurityToken version at implementation time, same "confirmed, not assumed" bar the
    // rest of this plugin's AWS SDK usage holds itself to.
    //
    // AssumeRoleAWSCredentials/AssumeRoleAWSCredentialsOptions live in Amazon.Runtime (AWSSDK.Core),
    // not Amazon.SecurityToken -- the brief's original `using Amazon.SecurityToken` import for this
    // type doesn't resolve against the installed AWSSDK.SecurityToken 3.7.401.13 / AWSSDK.Core
    // 3.7.400.64 (confirmed via `strings` on both DLLs: only AWSSDK.Core's assembly contains the
    // type; AWSSDK.SecurityToken only has the deprecated STSAssumeRoleAWSCredentials, whose own
    // embedded doc comment says it "has been replaced by Amazon.Runtime.AssumeRoleAWSCredentials").
    private static AWSCredentials BuildAssumeRoleCredentials(IReadOnlyDictionary<string, string> parsed)
    {
        var baseCredentials = FallbackCredentialsFactory.GetCredentials();
        var roleArn = parsed.GetValueOrDefault("roleArn", "");
        var sessionName = parsed.GetValueOrDefault("sessionName", "sbconsole");
        var options = new AssumeRoleAWSCredentialsOptions();
        if (parsed.TryGetValue("externalId", out var externalId))
        {
            options.ExternalId = externalId;
        }

        return new AssumeRoleAWSCredentials(baseCredentials, roleArn, sessionName, options);
    }
}
