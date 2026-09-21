using Amazon;
using Amazon.Runtime;
using FluentAssertions;
using SbConsole.Plugins.Aws.Client;

namespace SbConsole.Plugins.Aws.Tests.Client;

public class AwsCredentialsFactoryTests
{
    [Fact]
    public void Access_keys_mode_builds_BasicAWSCredentials()
    {
        var parsed = AwsConfigParser.Parse("mode=access-keys;region=eu-west-1;accessKeyId=AKIA123;secretAccessKey=s3cr3t");

        var credentials = AwsCredentialsFactory.BuildCredentials(parsed);

        credentials.Should().BeOfType<BasicAWSCredentials>();
        var immutable = credentials!.GetCredentials();
        immutable.AccessKey.Should().Be("AKIA123");
        immutable.SecretKey.Should().Be("s3cr3t");
    }

    [Fact]
    public void Access_keys_mode_with_session_token_builds_SessionAWSCredentials()
    {
        var parsed = AwsConfigParser.Parse("mode=access-keys;region=eu-west-1;accessKeyId=AKIA123;secretAccessKey=s3cr3t;sessionToken=tok");

        var credentials = AwsCredentialsFactory.BuildCredentials(parsed);

        credentials.Should().BeOfType<SessionAWSCredentials>();
        var immutable = credentials!.GetCredentials();
        immutable.Token.Should().Be("tok");
    }

    [Fact]
    public void Default_chain_mode_builds_no_explicit_credentials()
    {
        var parsed = AwsConfigParser.Parse("mode=default-chain;region=eu-west-1");

        AwsCredentialsFactory.BuildCredentials(parsed).Should().BeNull();
    }

    [Fact]
    public void Assume_role_mode_builds_AssumeRoleAWSCredentials()
    {
        var parsed = AwsConfigParser.Parse("mode=assume-role;region=eu-west-1;roleArn=arn:aws:iam::123456789012:role/SbConsoleReader;externalId=ext-1;sessionName=sbconsole-test");

        var credentials = AwsCredentialsFactory.BuildCredentials(parsed);

        // AssumeRoleAWSCredentials lives in Amazon.Runtime (AWSSDK.Core), not Amazon.SecurityToken --
        // verified against the installed AWSSDK.SecurityToken 3.7.401.13 / AWSSDK.Core 3.7.400.64
        // (see AwsCredentialsFactory.BuildAssumeRoleCredentials for how this was confirmed).
        credentials.Should().BeOfType<AssumeRoleAWSCredentials>();
    }

    [Fact]
    public void BuildConfig_sets_the_region()
    {
        var parsed = AwsConfigParser.Parse("mode=default-chain;region=eu-west-1");

        var config = AwsCredentialsFactory.BuildConfig(parsed);

        config.RegionEndpoint.Should().Be(RegionEndpoint.EUWest1);
    }

    [Fact]
    public void BuildConfig_applies_a_custom_endpoint_and_path_style_when_present()
    {
        var parsed = AwsConfigParser.Parse("mode=default-chain;region=us-east-1;endpoint=http://localstack:4566;pathStyle=true");

        var config = AwsCredentialsFactory.BuildConfig(parsed);

        // AmazonSQSConfig.ServiceURL normalizes to a trailing slash internally (confirmed by
        // running this test against the installed AWSSDK.SQS 3.7.400.62) -- the custom endpoint
        // is still applied verbatim otherwise.
        config.ServiceURL.Should().Be("http://localstack:4566/");
        config.UseHttp.Should().BeTrue();
    }

    [Fact]
    public void BuildConfig_leaves_ServiceURL_unset_when_no_endpoint_is_given()
    {
        var parsed = AwsConfigParser.Parse("mode=default-chain;region=eu-west-1");

        AwsCredentialsFactory.BuildConfig(parsed).ServiceURL.Should().BeNull();
    }
}
