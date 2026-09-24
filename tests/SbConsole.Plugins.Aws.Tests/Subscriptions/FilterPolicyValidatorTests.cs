using FluentAssertions;
using SbConsole.Plugins.Aws.Subscriptions;

namespace SbConsole.Plugins.Aws.Tests.Subscriptions;

public class FilterPolicyValidatorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n ")]
    public void Empty_or_whitespace_means_clear(string? policy)
    {
        var result = FilterPolicyValidator.Validate(policy, FilterPolicyValidator.MessageAttributes);

        result.IsValid.Should().BeTrue();
        result.IsClear.Should().BeTrue();
        result.Error.Should().BeNull();
    }

    [Theory]
    [InlineData(FilterPolicyValidator.MessageAttributes)]
    [InlineData(FilterPolicyValidator.MessageBody)]
    public void A_json_object_is_valid_under_either_scope(string scope)
    {
        var result = FilterPolicyValidator.Validate("""{"region":["uk"]}""", scope);

        result.IsValid.Should().BeTrue();
        result.IsClear.Should().BeFalse();
        result.Error.Should().BeNull();
    }

    [Theory]
    [InlineData("""["uk"]""")]
    [InlineData("42")]
    [InlineData("\"uk\"")]
    [InlineData("null")]
    public void Rejects_json_that_is_not_an_object(string policy)
    {
        var result = FilterPolicyValidator.Validate(policy, FilterPolicyValidator.MessageAttributes);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("JSON object");
    }

    [Fact]
    public void Rejects_invalid_json_with_a_friendly_message()
    {
        var result = FilterPolicyValidator.Validate("""{"region": ["uk"]""", FilterPolicyValidator.MessageAttributes);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Be("Filter policy isn't valid JSON.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("messagebody")]
    [InlineData("Body")]
    public void Rejects_an_unknown_scope(string scope)
    {
        var result = FilterPolicyValidator.Validate("""{"region":["uk"]}""", scope);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("MessageAttributes or MessageBody");
    }
}
