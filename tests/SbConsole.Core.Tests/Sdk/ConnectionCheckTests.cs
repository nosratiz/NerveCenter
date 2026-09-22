using FluentAssertions;
using SbConsole.Sdk;

namespace SbConsole.Core.Tests.Sdk;

public class ConnectionCheckTests
{
    [Fact]
    public void Passed_check_carries_an_optional_detail()
    {
        var check = new ConnectionCheck("Queues visible", ConnectionCheckStatus.Passed, "12");

        check.Label.Should().Be("Queues visible");
        check.Status.Should().Be(ConnectionCheckStatus.Passed);
        check.Detail.Should().Be("12");
    }

    [Fact]
    public void Detail_is_optional()
    {
        var check = new ConnectionCheck("Topics visible", ConnectionCheckStatus.Failed);

        check.Detail.Should().BeNull();
    }
}
