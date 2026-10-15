using FluentAssertions;
using SbConsole.Plugins.Kubernetes.Architecture;
using SbConsole.Plugins.Kubernetes.Client;
using static SbConsole.Plugins.Kubernetes.Tests.Architecture.ArchTestData;

namespace SbConsole.Plugins.Kubernetes.Tests.Architecture;

public class ArchitectureLayoutTests
{
    private static readonly ArchLayoutOptions Defaults = new(ArchGroupBy.Namespace, ShowStores: true, ShowExternal: true);

    private static ArchLayout Layout(ArchLayoutOptions? options = null, ArchitectureGraph? graph = null) =>
        ArchitectureLayout.Compute(graph ?? ArchitectureBuilder.Build(Cluster(), Scope, ArchOverlay.Empty), options ?? Defaults);

    private static PlacedNode Placed(ArchLayout layout, string name) => layout.Nodes.Single(n => n.Node.Name == name);

    [Fact]
    public void Columns_follow_dependency_depth()
    {
        var layout = Layout();

        var x = (string name) => Placed(layout, name).X;
        x("shop").Should().BeLessThan(x("checkout-api"));
        x("checkout-api").Should().BeLessThan(x("payment-gateway"));
        x("payment-gateway").Should().Be(x("cart"));
        x("settlement-api").Should().Be(x("shop"), "roots sit in column 0");
    }

    [Fact]
    public void Namespaces_become_lanes_and_stores_go_to_the_side_column()
    {
        var layout = Layout();

        layout.Lanes.Where(l => !l.Side).Select(l => l.Label).Should().Equal("checkout", "payments");
        var side = layout.Lanes.Single(l => l.Side);
        side.Label.Should().Be("stores · brokers · external");
        foreach (var name in new[] { "cart-redis", "kafka", "pg-main", "hooks.example.com" })
        {
            var node = Placed(layout, name);
            node.X.Should().BeGreaterThanOrEqualTo(side.X);
            node.Y.Should().BeInRange(side.Y, side.Y + side.Height);
        }

        var payments = layout.Lanes.Single(l => l.Label == "payments");
        Placed(layout, "ledger").Y.Should().BeInRange(payments.Y, payments.Y + payments.Height);
        layout.Nodes.Select(n => n.Node.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Nodes_in_a_lane_do_not_overlap()
    {
        var layout = Layout();

        var boxes = layout.Nodes.Select(n => (n.X, n.Y, R: n.X + n.Width, B: n.Y + ArchitectureLayout.NodeHeight)).ToList();
        for (var i = 0; i < boxes.Count; i++)
        {
            for (var j = i + 1; j < boxes.Count; j++)
            {
                var (a, b) = (boxes[i], boxes[j]);
                (a.X < b.R && b.X < a.R && a.Y < b.B && b.Y < a.B).Should().BeFalse($"{layout.Nodes[i].Node.Name} overlaps {layout.Nodes[j].Node.Name}");
            }
        }

        layout.Width.Should().BeGreaterThanOrEqualTo(layout.Nodes.Max(n => n.X + n.Width));
        layout.Height.Should().BeGreaterThanOrEqualTo(layout.Nodes.Max(n => n.Y + ArchitectureLayout.NodeHeight));
    }

    [Fact]
    public void Team_grouping_uses_the_team_label_and_puts_unlabelled_last()
    {
        var layout = Layout(Defaults with { GroupBy = ArchGroupBy.Team });

        layout.Lanes.Where(l => !l.Side).Select(l => l.Label).Should().Equal("payments", "storefront", "no team");
    }

    [Fact]
    public void No_grouping_is_one_unlabelled_lane()
    {
        var layout = Layout(Defaults with { GroupBy = ArchGroupBy.None });

        layout.Lanes.Where(l => !l.Side).Should().ContainSingle().Which.Label.Should().BeEmpty();
    }

    [Fact]
    public void Toggles_hide_stores_or_externals_and_their_edges()
    {
        var layout = Layout(Defaults with { ShowStores = false, ShowExternal = false });

        layout.Nodes.Should().NotContain(n => n.Node.Kind == ArchNodeKind.Store || n.Node.Kind == ArchNodeKind.External);
        var visible = layout.Nodes.Select(n => n.Node.Id).ToHashSet();
        layout.Edges.Should().OnlyContain(e => visible.Contains(e.Edge.From) && visible.Contains(e.Edge.To));
        layout.Lanes.Should().NotContain(l => l.Side);
    }

    [Fact]
    public void Edges_run_from_the_right_edge_of_the_source_to_the_left_edge_of_the_target()
    {
        var layout = Layout();
        var from = Placed(layout, "checkout-api");
        var to = Placed(layout, "payment-gateway");

        var edge = layout.Edges.Single(e => e.Edge.From == from.Node.Id && e.Edge.To == to.Node.Id);

        var y1 = from.Y + ArchitectureLayout.NodeHeight / 2;
        var y2 = to.Y + ArchitectureLayout.NodeHeight / 2;
        edge.Path.Should().StartWith($"M{from.X + from.Width} {y1} C").And.EndWith($" {to.X} {y2}");
    }

    [Fact]
    public void Cycles_are_laid_out_once_without_hanging()
    {
        var inputs = new K8sArchitectureInputs(
            [Workload("a", "x", env: [("Y", "http://y:80")]), Workload("a", "y", env: [("X", "http://x:80")])],
            [Service("a", "x", port: 80), Service("a", "y", port: 80)], []);

        var layout = Layout(graph: ArchitectureBuilder.Build(inputs, ["a"], ArchOverlay.Empty));

        layout.Nodes.Should().HaveCount(2);
        layout.Edges.Should().HaveCount(2);
        Placed(layout, "x").X.Should().NotBe(Placed(layout, "y").X);
    }

    [Fact]
    public void An_empty_graph_has_no_lanes()
    {
        var layout = Layout(graph: ArchitectureGraph.Empty);

        layout.Nodes.Should().BeEmpty();
        layout.Lanes.Should().BeEmpty();
    }
}
