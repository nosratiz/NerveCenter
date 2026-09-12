using FluentAssertions;
using SbConsole.Sdk;

namespace SbConsole.Core.Tests.Sdk;

public class ConnectionTestResultTests
{
    [Fact]
    public void Success_result_has_no_error_message_by_default()
    {
        var result = new ConnectionTestResult(Success: true);

        result.Success.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void Failure_result_carries_an_error_message()
    {
        var result = new ConnectionTestResult(Success: false, ErrorMessage: "Unauthorized (401)");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Unauthorized (401)");
    }
}
