using FluentAssertions;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Plugins.Kubernetes.Events;

namespace SbConsole.Plugins.Kubernetes.Tests.Events;

public class EventGroupingTests
{
    private static readonly DateTimeOffset T0 = TestData.T0;

    private static IReadOnlyList<K8sEvent> Warnings() => TestData.Events().Where(e => e.Type == "Warning").ToList();

    private static K8sEvent Ev(string type, string reason, string name, int minutesAgo, int count = 1, string kind = "Pod") =>
        new("checkout", type, reason, $"{reason} message", new K8sRef(kind, "checkout", name), count, T0.AddMinutes(-minutesAgo), "kubelet");

    [Fact]
    public void By_involved_object_groups_newest_object_first()
    {
        var groups = EventGrouping.Group(Warnings(), EventGroupBy.InvolvedObject);

        groups.Should().HaveCount(9);
        groups.Select(g => g.Object!.Name).Should().Equal(
            "payment-gateway-7d9f8-x2k4q", "prometheus-1", "payment-gateway", "aks-pool1-38211-vmss00000a",
            "checkout-api-6c8d7-r9p2m", "aks-pool1-38211-vmss000004", "checkout-api", "report-builder-2x8ql", "settlement-20261014-0200");
        groups.Select(g => g.Newest).Should().BeInDescendingOrder();
        groups[0].Key.Should().Be("Pod/checkout/payment-gateway-7d9f8-x2k4q");
        groups[0].Object.Should().Be(new K8sRef("Pod", "checkout", "payment-gateway-7d9f8-x2k4q"));
        groups[0].Reason.Should().BeNull();
        groups[0].Newest.Should().Be(T0.AddMinutes(-2));
    }

    [Fact]
    public void Same_name_different_kind_is_a_different_object()
    {
        var groups = EventGrouping.Group(Warnings(), EventGroupBy.InvolvedObject);

        groups.Where(g => g.Object!.Name == "checkout-api").Should().ContainSingle()
            .Which.Object!.Kind.Should().Be("HorizontalPodAutoscaler");
        groups.Where(g => g.Object!.Name == "payment-gateway").Should().ContainSingle()
            .Which.Object!.Kind.Should().Be("Deployment");
    }

    [Fact]
    public void Events_in_a_group_are_newest_first()
    {
        var gateway = EventGrouping.Group(Warnings(), EventGroupBy.InvolvedObject)[0];

        gateway.Events.Select(e => e.Reason).Should().Equal("BackOff", "Unhealthy");
        var node = EventGrouping.Group(Warnings(), EventGroupBy.InvolvedObject).Single(g => g.Object!.Name == "aks-pool1-38211-vmss00000a");
        node.Events.Select(e => e.LastSeen).Should().BeInDescendingOrder();
        node.Events.Should().HaveCount(3);
    }

    [Fact]
    public void Warning_count_sums_the_event_counts_of_warnings_only()
    {
        var events = new[]
        {
            Ev("Warning", "BackOff", "api-1", 2, 14),
            Ev("Warning", "Unhealthy", "api-1", 9, 2),
            Ev("Normal", "Pulled", "api-1", 1, 5),
        };

        var group = EventGrouping.Group(events, EventGroupBy.InvolvedObject).Should().ContainSingle().Subject;

        group.WarningCount.Should().Be(16);
        group.Events.Should().HaveCount(3);
        group.Newest.Should().Be(T0.AddMinutes(-1));
    }

    [Fact]
    public void Top_reasons_are_up_to_two_distinct_reasons_by_count()
    {
        var events = new[]
        {
            Ev("Warning", "Unhealthy", "api-1", 1, 2),
            Ev("Warning", "BackOff", "api-1", 2, 14),
            Ev("Warning", "Unhealthy", "api-1", 3, 3),
            Ev("Warning", "FailedMount", "api-1", 4, 1),
        };

        var group = EventGrouping.Group(events, EventGroupBy.InvolvedObject).Single();

        group.TopReasons.Should().Equal("BackOff", "Unhealthy");
    }

    [Fact]
    public void Top_reasons_of_a_single_reason_group_has_one_entry()
    {
        var group = EventGrouping.Group(Warnings(), EventGroupBy.InvolvedObject).Single(g => g.Object!.Name == "report-builder-2x8ql");

        group.TopReasons.Should().Equal("FailedMount");
        group.WarningCount.Should().Be(26);
    }

    [Fact]
    public void By_reason_groups_across_objects()
    {
        var groups = EventGrouping.Group(Warnings(), EventGroupBy.Reason);

        groups[0].Key.Should().Be("BackOff");
        groups[0].Reason.Should().Be("BackOff");
        groups[0].Object.Should().BeNull();
        var unhealthy = groups.Single(g => g.Key == "Unhealthy");
        unhealthy.Events.Select(e => e.Regarding.Name).Should().BeEquivalentTo("payment-gateway-7d9f8-x2k4q", "checkout-api-6c8d7-r9p2m");
        unhealthy.WarningCount.Should().Be(5);
        unhealthy.TopReasons.Should().Equal("Unhealthy");
        groups.Select(g => g.Newest).Should().BeInDescendingOrder();
        groups.Select(g => g.Key).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void None_is_one_group_with_every_event_newest_first()
    {
        var shuffled = TestData.Events().Reverse().ToList();

        var groups = EventGrouping.Group(shuffled, EventGroupBy.None);

        var all = groups.Should().ContainSingle().Subject;
        all.Key.Should().Be("");
        all.Object.Should().BeNull();
        all.Reason.Should().BeNull();
        all.Events.Should().HaveCount(43);
        all.Events.Select(e => e.LastSeen).Should().BeInDescendingOrder();
        all.WarningCount.Should().Be(Warnings().Sum(e => e.Count));
    }

    [Fact]
    public void No_events_are_no_groups()
    {
        EventGrouping.Group([], EventGroupBy.InvolvedObject).Should().BeEmpty();
        EventGrouping.Group([], EventGroupBy.Reason).Should().BeEmpty();
        EventGrouping.Group([], EventGroupBy.None).Should().BeEmpty();
    }
}
