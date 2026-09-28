using FluentAssertions;
using SbConsole.Plugins.Redis.Components;

namespace SbConsole.Plugins.Redis.Tests.Components;

public class RedisSecretFieldMaskingTests
{
    [Theory]
    [InlineData("csrf_token", true)]
    [InlineData("CSRF", true)]
    [InlineData("authToken", true)]
    [InlineData("client_secret", true)]
    [InlineData("SECRET_KEY", true)]
    [InlineData("username", false)]
    [InlineData("expires_at", false)]
    [InlineData("email", false)]
    public void Flags_field_names_containing_a_sensitive_pattern_case_insensitively(string field, bool expected)
    {
        RedisSecretFieldMasking.IsSensitive(field).Should().Be(expected);
    }
}
