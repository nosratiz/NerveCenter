using FluentAssertions;
using SbConsole.Sdk;

namespace SbConsole.Core.Tests.Sdk;

public class PluginContributionTests
{
    [Fact]
    public void Holds_page_and_action_counts()
    {
        var contribution = new PluginContribution(PageCount: 3, ActionCount: 8);

        contribution.PageCount.Should().Be(3);
        contribution.ActionCount.Should().Be(8);
    }
}
