using FluentAssertions;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests;

public class PluginResultTests
{
    [Fact]
    public void Fail_from_an_exception_uses_the_message_not_the_stack_trace()
    {
        Exception thrown;
        try
        {
            throw new InvalidOperationException("broker said no");
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        PluginResult.Fail(thrown).Error.Should().Be("broker said no");
        PluginResult<int>.Fail(thrown).Error.Should().Be("broker said no");
        PluginResult.Fail(thrown).Error.Should().NotContain("   at ");
    }

    [Fact]
    public void Fail_from_an_exception_caps_a_flood_of_sdk_text()
    {
        var ex = new InvalidOperationException(new string('x', 3_000));

        PluginResult.Fail(ex).Error.Should().EndWith(FriendlyError.TruncationMarker);
        PluginResult<int>.Fail(ex).Error!.Length
            .Should().Be(FriendlyError.MaxLength + FriendlyError.TruncationMarker.Length);
    }

    [Fact]
    public void Fail_from_a_string_is_left_alone_for_the_handlers_own_short_messages()
    {
        PluginResult.Fail("Connection not found.").Error.Should().Be("Connection not found.");
    }
}
