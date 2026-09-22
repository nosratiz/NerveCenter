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

    [Fact]
    public void Existing_two_argument_construction_still_compiles_with_null_identity_and_checks()
    {
        var result = new ConnectionTestResult(Success: true, ErrorMessage: null);

        result.Identity.Should().BeNull();
        result.Checks.Should().BeNull();
    }

    [Fact]
    public void Success_result_can_carry_identity_and_passed_checks()
    {
        var result = new ConnectionTestResult(
            Success: true,
            Identity: "123456789012",
            Checks: [new ConnectionCheck("Queues visible", ConnectionCheckStatus.Passed, "12")]);

        result.Identity.Should().Be("123456789012");
        result.Checks.Should().ContainSingle(c => c.Label == "Queues visible" && c.Status == ConnectionCheckStatus.Passed);
    }

    [Fact]
    public void Under_permissioned_result_is_success_true_with_a_failed_check()
    {
        var result = new ConnectionTestResult(
            Success: true,
            Identity: "123456789012",
            Checks: [new ConnectionCheck("Topics visible", ConnectionCheckStatus.Failed, "sns:ListTopics denied")]);

        result.Success.Should().BeTrue();
        result.Checks.Should().ContainSingle(c => c.Status == ConnectionCheckStatus.Failed);
    }
}
