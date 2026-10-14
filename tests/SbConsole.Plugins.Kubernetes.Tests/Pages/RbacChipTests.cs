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
}
