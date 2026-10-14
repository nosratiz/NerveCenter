using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Sdk;
using static SbConsole.Plugins.Kubernetes.Tests.TestData;

namespace SbConsole.Plugins.Kubernetes.Tests;

public class KubernetesPluginDashboardTests
{
    private readonly IKubernetesOperations _ops = Substitute.For<IKubernetesOperations>();
    private readonly FakeTimeProvider _clock = new(T0);

    private KubernetesPlugin Plugin()
    {
        var plugin = new KubernetesPlugin();
        plugin.InitializeForTesting(_ops, _clock);
        return plugin;
    }

    private void Returns(string secret, K8sOverview overview) =>
        _ops.GetOverviewAsync(secret, Arg.Is<IReadOnlyList<string>>(n => n.Count == 0), Arg.Any<CancellationToken>()).Returns(overview);

    private static K8sOverview Troubled() => OverviewWith(
        nodes: [Node("n1"), Node("n2"), Node("bad", ready: "False")],
        problemPods:
        [
            Pod(name: "c1", status: "CrashLoopBackOff", restarts: 9),
            Pod(name: "c2", status: "CrashLoopBackOff", restarts: 2),
            Pod(name: "f", phase: "Failed"),
            Pod(name: "u", phase: "Pending", unschedulable: "no room"),
        ],
        problemWorkloads: [Deployment(name: "web", available: 0), Job()]);

    [Fact]
    public async Task Metrics_report_nodes_ready_and_crash_loops()
    {
        Returns("s", Troubled());

        var metrics = await Plugin().GetDashboardMetricsAsync("s");

        metrics.Should().Equal(new PluginDashboardMetric("Nodes ready", 2), new PluginDashboardMetric("Crash loops", 2));
    }

    [Fact]
    public async Task Metrics_are_empty_on_failure_or_when_not_initialized()
    {
        _ops.GetOverviewAsync("s", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("x"));

        (await Plugin().GetDashboardMetricsAsync("s")).Should().BeEmpty();
        (await new KubernetesPlugin().GetDashboardMetricsAsync("s")).Should().BeEmpty();
    }

    [Fact]
    public async Task Problems_are_the_top_five_attention_items_without_connection_id()
    {
        Returns("s", Troubled());

        var problems = await Plugin().GetDashboardProblemsAsync(Guid.NewGuid(), "s", Substitute.For<IPluginStore>());

        problems.Should().Equal(
            new PluginDashboardProblem("Error", "c1", "9 restarts", KubernetesPlugin.PodHref("checkout", "c1")),
            new PluginDashboardProblem("Error", "c2", "2 restarts", KubernetesPlugin.PodHref("checkout", "c2")),
            new PluginDashboardProblem("Error", "bad", "NotReady for 3d · 10 pods affected", KubernetesPlugin.NodesHref),
            new PluginDashboardProblem("Error", "web", "0/3 available", KubernetesPlugin.DeploymentHref("checkout", "web")),
            new PluginDashboardProblem("Error", "migrate", "BackoffLimitExceeded", KubernetesPlugin.WorkloadsHref + "?tab=jobs"));
    }

    [Fact]
    public async Task Problems_are_empty_when_not_initialized() =>
        (await new KubernetesPlugin().GetDashboardProblemsAsync(Guid.NewGuid(), "s", Substitute.For<IPluginStore>())).Should().BeEmpty();

    [Fact]
    public async Task Nav_badge_on_overview_is_the_attention_count()
    {
        Returns("s", Troubled());
        var plugin = Plugin();

        (await plugin.GetNavBadgeAsync(KubernetesPlugin.OverviewHref, "s")).Should().Be(6);
        (await plugin.GetNavBadgeAsync(KubernetesPlugin.NodesHref, "s")).Should().BeNull();
    }

    [Fact]
    public async Task Nav_badge_is_null_when_zero_uninitialized_or_throwing()
    {
        (await new KubernetesPlugin().GetNavBadgeAsync(KubernetesPlugin.OverviewHref, "s")).Should().BeNull();

        Returns("healthy", OverviewWith());
        (await Plugin().GetNavBadgeAsync(KubernetesPlugin.OverviewHref, "healthy")).Should().BeNull();

        _ops.GetOverviewAsync("down", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("x"));
        (await Plugin().GetNavBadgeAsync(KubernetesPlugin.OverviewHref, "down")).Should().BeNull();
    }

    [Fact]
    public async Task Nav_badge_is_cached_for_fifteen_seconds_per_secret()
    {
        Returns("s", Troubled());
        Returns("other", OverviewWith(problemWorkloads: [Job()]));
        var plugin = Plugin();

        (await plugin.GetNavBadgeAsync(KubernetesPlugin.OverviewHref, "s")).Should().Be(6);
        _clock.Advance(TimeSpan.FromSeconds(14));
        (await plugin.GetNavBadgeAsync(KubernetesPlugin.OverviewHref, "s")).Should().Be(6);
        await _ops.Received(1).GetOverviewAsync("s", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());

        (await plugin.GetNavBadgeAsync(KubernetesPlugin.OverviewHref, "other")).Should().Be(1);

        _clock.Advance(TimeSpan.FromSeconds(2));
        Returns("s", OverviewWith());
        (await plugin.GetNavBadgeAsync(KubernetesPlugin.OverviewHref, "s")).Should().BeNull();
        await _ops.Received(2).GetOverviewAsync("s", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Metrics_problems_and_badge_share_one_overview_for_fifteen_seconds()
    {
        Returns("s", Troubled());
        var plugin = Plugin();

        await plugin.GetDashboardMetricsAsync("s");
        await plugin.GetDashboardProblemsAsync(Guid.NewGuid(), "s", Substitute.For<IPluginStore>());
        await plugin.GetNavBadgeAsync(KubernetesPlugin.OverviewHref, "s");
        await _ops.Received(1).GetOverviewAsync("s", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());

        _clock.Advance(TimeSpan.FromSeconds(16));
        await plugin.GetDashboardMetricsAsync("s");
        await _ops.Received(2).GetOverviewAsync("s", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_overview_is_cached_too()
    {
        _ops.GetOverviewAsync("down", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("x"));
        var plugin = Plugin();

        (await plugin.GetDashboardMetricsAsync("down")).Should().BeEmpty();
        (await plugin.GetDashboardProblemsAsync(Guid.NewGuid(), "down", Substitute.For<IPluginStore>())).Should().BeEmpty();
        (await plugin.GetNavBadgeAsync(KubernetesPlugin.OverviewHref, "down")).Should().BeNull();
        await _ops.Received(1).GetOverviewAsync("down", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }
}
