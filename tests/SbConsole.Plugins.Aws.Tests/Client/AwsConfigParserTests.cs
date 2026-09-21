using FluentAssertions;
using SbConsole.Plugins.Aws.Client;

namespace SbConsole.Plugins.Aws.Tests.Client;

public class AwsConfigParserTests
{
    [Fact]
    public void Empty_string_parses_to_an_empty_dictionary()
    {
        AwsConfigParser.Parse("").Should().BeEmpty();
    }

    [Fact]
    public void Parses_access_keys_mode()
    {
        var result = AwsConfigParser.Parse("mode=access-keys;region=eu-west-1;accessKeyId=AKIA123;secretAccessKey=s3cr3t");

        result.Should().Equal(new Dictionary<string, string>
        {
            ["mode"] = "access-keys",
            ["region"] = "eu-west-1",
            ["accessKeyId"] = "AKIA123",
            ["secretAccessKey"] = "s3cr3t",
        });
    }

    [Fact]
    public void Ignores_a_trailing_semicolon()
    {
        AwsConfigParser.Parse("region=eu-west-1;")
            .Should().Equal(new Dictionary<string, string> { ["region"] = "eu-west-1" });
    }

    [Fact]
    public void Skips_a_malformed_segment_with_no_equals_sign()
    {
        AwsConfigParser.Parse("region=eu-west-1;not-a-pair;mode=default-chain")
            .Should().Equal(new Dictionary<string, string>
            {
                ["region"] = "eu-west-1",
                ["mode"] = "default-chain",
            });
    }

    [Fact]
    public void Later_duplicate_keys_win()
    {
        AwsConfigParser.Parse("region=eu-west-1;region=us-east-1")
            .Should().Equal(new Dictionary<string, string> { ["region"] = "us-east-1" });
    }

    [Fact]
    public void Trims_whitespace_around_keys_and_values()
    {
        AwsConfigParser.Parse(" region = eu-west-1 ; mode = default-chain ")
            .Should().Equal(new Dictionary<string, string>
            {
                ["region"] = "eu-west-1",
                ["mode"] = "default-chain",
            });
    }

    [Fact]
    public void SafeEcho_echoes_only_the_allowlisted_keys_in_a_fixed_order()
    {
        AwsConfigParser.SafeEcho("secretAccessKey=s3cr3t;endpoint=http://localstack:4566;region=eu-west-1;accessKeyId=AKIA123;mode=access-keys")
            .Should().Be("region=eu-west-1 · mode=access-keys · endpoint=http://localstack:4566");
    }

    [Fact]
    public void SafeEcho_omits_keys_that_are_absent()
    {
        AwsConfigParser.SafeEcho("region=eu-west-1;mode=default-chain")
            .Should().Be("region=eu-west-1 · mode=default-chain");
    }

    [Fact]
    public void SafeEcho_never_echoes_credentials_or_role_fields()
    {
        AwsConfigParser.SafeEcho("accessKeyId=AKIA123;secretAccessKey=s3cr3t;sessionToken=tok;roleArn=arn:aws:iam::123456789012:role/X;externalId=ext;sessionName=sess")
            .Should().Be("");
    }

    [Fact]
    public void SafeEcho_of_an_empty_config_is_empty()
    {
        AwsConfigParser.SafeEcho("").Should().Be("");
    }
}
