using FluentAssertions;
using SbConsole.Sdk;

namespace SbConsole.Core.Tests.Sdk;

public class ActionRiskTests
{
    [Fact]
    public void Risk_levels_are_ordered_safe_to_destructive()
    {
        ((int)ActionRisk.Safe).Should().Be(0);
        ((int)ActionRisk.Mutating).Should().Be(1);
        ((int)ActionRisk.Destructive).Should().Be(2);
        (ActionRisk.Destructive > ActionRisk.Mutating).Should().BeTrue();
    }
}
