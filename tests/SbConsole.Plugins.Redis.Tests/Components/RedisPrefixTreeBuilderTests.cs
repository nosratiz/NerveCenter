using FluentAssertions;
using SbConsole.Plugins.Redis.Components;

namespace SbConsole.Plugins.Redis.Tests.Components;

public class RedisPrefixTreeBuilderTests
{
    [Fact]
    public void Groups_keys_into_a_tree_split_on_the_delimiter()
    {
        var keys = new[] { "session:1:csrf", "session:1:data", "session:2:data", "jobs:1" };

        var tree = RedisPrefixTreeBuilder.Build(keys, ':');

        tree.Should().HaveCount(2);
        var session = tree.Single(n => n.Segment == "session");
        session.KeyCount.Should().Be(3);
        session.FullPrefix.Should().Be("session");
        session.Children.Should().HaveCount(2);
        var session1 = session.Children.Single(n => n.Segment == "1");
        session1.KeyCount.Should().Be(2);
        session1.FullPrefix.Should().Be("session:1");
        session1.Children.Select(c => c.Segment).Should().BeEquivalentTo(["csrf", "data"]);

        var jobs = tree.Single(n => n.Segment == "jobs");
        jobs.KeyCount.Should().Be(1);
    }

    [Fact]
    public void A_key_with_no_delimiter_is_its_own_leaf_at_the_top_level()
    {
        var tree = RedisPrefixTreeBuilder.Build(["flags:beta", "standalone-key"], ':');

        tree.Should().Contain(n => n.Segment == "standalone-key" && n.Keys.Contains("standalone-key"));
    }

    [Fact]
    public void Leaf_nodes_carry_the_full_original_key()
    {
        var tree = RedisPrefixTreeBuilder.Build(["lb:us-east:weights"], ':');

        var lb = tree.Single();
        var usEast = lb.Children.Single();
        var weights = usEast.Children.Single();
        weights.Keys.Should().Equal("lb:us-east:weights");
        weights.KeyCount.Should().Be(1);
    }

    [Fact]
    public void An_empty_key_list_yields_an_empty_tree()
    {
        RedisPrefixTreeBuilder.Build([], ':').Should().BeEmpty();
    }

    [Fact]
    public void Top_level_nodes_are_sorted_ordinally()
    {
        var tree = RedisPrefixTreeBuilder.Build(["zeta:1", "alpha:1", "mid:1"], ':');

        tree.Select(n => n.Segment).Should().Equal("alpha", "mid", "zeta");
    }
}
