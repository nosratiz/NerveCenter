using Bunit;
using FluentAssertions;
using SbConsole.Plugins.Kubernetes.Access;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Plugins.Kubernetes.Components;

namespace SbConsole.Plugins.Kubernetes.Tests.Pages;

public class RbacChipTests : K8sPageTestBase
{
    private IRenderedComponent<RbacChip> RenderChip(K8sPermissions? permissions) =>
        Render<RbacChip>(p => p
            .Add(c => c.Permissions, permissions)
            .Add(c => c.Needs, K8sPermissions.WorkloadActions));

    [Fact]
    public void Renders_nothing_without_permissions()
    {
        RenderChip(null).Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void Renders_nothing_for_an_admin()
    {
        RenderChip(K8sPermissions.From(TestData.AdminAccess())).Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void A_viewer_gets_a_read_only_chip_whose_tooltip_lists_what_is_missing()
    {
        var cut = RenderChip(K8sPermissions.From(TestData.ViewerAccess()));

        cut.Find(".k8s-rbac-chip").TextContent.Should().Contain("read-only (RBAC)");
        cut.Instance.TooltipText.Should()
            .StartWith("Connected as viewer. Missing for this page: ")
            .And.Contain("deployments/scale · patch")
            .And.EndWith(". Reads work normally.");
    }

    [Fact]
    public void An_account_with_some_writes_gets_a_limited_chip()
    {
        var facts = new K8sAccessFacts("ops", [new K8sRulesReview("default", false,
            [new K8sRule([""], ["pods"], ["get", "list", "delete"], [])])], new Dictionary<string, bool>());

        var cut = RenderChip(K8sPermissions.From(facts));

        cut.Find(".k8s-rbac-chip").TextContent.Should().Contain("limited (RBAC)");
    }

    [Fact]
    public void Cluster_needs_the_account_fails_are_listed_in_the_tooltip()
    {
        var admin = TestData.AdminAccess("ops");
        var facts = admin with
        {
            ClusterChecks = new Dictionary<string, bool>(admin.ClusterChecks) { ["nodes/patch"] = false, ["pods/eviction/create"] = false },
        };

        var cut = Render<RbacChip>(p => p
            .Add(c => c.Permissions, K8sPermissions.From(facts))
            .Add(c => c.ClusterNeeds, K8sPermissions.NodeClusterActions));

        cut.Find(".k8s-rbac-chip").TextContent.Should().Contain("limited (RBAC)");
        cut.Instance.TooltipText.Should().Be("Connected as ops. Missing for this page: nodes · patch, pods/eviction · create. Reads work normally.");
    }

    [Fact]
    public void Granted_cluster_needs_render_nothing()
    {
        var cut = Render<RbacChip>(p => p
            .Add(c => c.Permissions, K8sPermissions.From(TestData.AdminAccess()))
            .Add(c => c.ClusterNeeds, K8sPermissions.NodeClusterActions));

        cut.Markup.Trim().Should().BeEmpty();
    }
}
