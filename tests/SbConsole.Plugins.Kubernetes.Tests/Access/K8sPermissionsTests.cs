using FluentAssertions;
using SbConsole.Plugins.Kubernetes.Access;
using SbConsole.Plugins.Kubernetes.Client;

namespace SbConsole.Plugins.Kubernetes.Tests.Access;

public class K8sPermissionsTests
{
    private static K8sRule Rule(string[] groups, string[] resources, string[] verbs, string[]? names = null) =>
        new(groups, resources, verbs, names ?? []);

    private static K8sPermissions Perms(params (string Ns, K8sRule[] Rules)[] reviews) =>
        K8sPermissions.From(new K8sAccessFacts("alice",
            reviews.Select(r => new K8sRulesReview(r.Ns, false, r.Rules)).ToList(),
            new Dictionary<string, bool> { ["nodes/patch"] = true, ["nodes/list"] = false }));

    [Fact]
    public void Exact_match_grants()
    {
        var p = Perms(("default", [Rule(["apps"], ["deployments"], ["patch"])]));
        p.Can("apps", "deployments", "patch").Should().BeTrue();
        p.Can("apps", "deployments", "delete").Should().BeFalse();
        p.Can("batch", "deployments", "patch").Should().BeFalse();
    }

    [Fact]
    public void Wildcards_match_group_resource_and_verb()
    {
        Perms(("default", [Rule(["*"], ["deployments"], ["patch"])])).Can("apps", "deployments", "patch").Should().BeTrue();
        Perms(("default", [Rule(["apps"], ["*"], ["patch"])])).Can("apps", "deployments", "patch").Should().BeTrue();
        Perms(("default", [Rule(["apps"], ["deployments"], ["*"])])).Can("apps", "deployments", "delete").Should().BeTrue();
    }

    [Fact]
    public void Resource_names_rule_does_not_grant()
    {
        var p = Perms(("default", [Rule(["apps"], ["deployments"], ["patch"], ["web"])]));
        p.Can("apps", "deployments", "patch").Should().BeFalse();
    }

    [Fact]
    public void Subresource_needs_its_own_rule()
    {
        var p = Perms(("default", [Rule(["apps"], ["deployments"], ["patch"])]));
        p.Can("apps", "deployments/scale", "patch").Should().BeFalse();
        Perms(("default", [Rule(["apps"], ["deployments/scale"], ["patch"])])).Can("apps", "deployments/scale", "patch").Should().BeTrue();
        Perms(("default", [Rule(["apps"], ["deployments/*"], ["patch"])])).Can("apps", "deployments/scale", "patch").Should().BeTrue();
    }

    [Fact]
    public void Namespace_specific_grants()
    {
        var p = Perms(
            ("checkout", [Rule(["apps"], ["deployments"], ["patch"])]),
            ("payments", []));
        p.Can("apps", "deployments", "patch", "checkout").Should().BeTrue();
        p.Can("apps", "deployments", "patch", "payments").Should().BeFalse();
        p.Can("apps", "deployments", "patch").Should().BeFalse();
    }

    [Fact]
    public void Unreviewed_namespace_falls_back_to_default_only_when_default_was_the_sole_review()
    {
        var p = Perms(("default", [Rule(["apps"], ["deployments"], ["patch"])]));
        p.Can("apps", "deployments", "patch", "anything").Should().BeTrue();

        var q = Perms(("checkout", [Rule(["apps"], ["deployments"], ["patch"])]), ("default", [Rule(["apps"], ["deployments"], ["patch"])]));
        q.Can("apps", "deployments", "patch", "other").Should().BeFalse();
    }

    [Fact]
    public void Null_namespace_requires_every_review_to_grant()
    {
        var rule = Rule(["apps"], ["deployments"], ["patch"]);
        Perms(("a", [rule]), ("b", [rule])).Can("apps", "deployments", "patch").Should().BeTrue();
    }

    [Fact]
    public void Cluster_checks_missing_key_is_false()
    {
        var p = Perms(("default", []));
        p.CanCluster("nodes/patch").Should().BeTrue();
        p.CanCluster("nodes/list").Should().BeFalse();
        p.CanCluster("nope").Should().BeFalse();
    }

    [Fact]
    public void View_like_rules_are_read_only()
    {
        var p = Perms(("default", [Rule(["", "apps"], ["pods", "deployments"], ["get", "list", "watch"])]));
        p.IsReadOnly.Should().BeTrue();
    }

    [Fact]
    public void View_role_plus_basic_user_self_review_rules_is_read_only()
    {
        var p = Perms(("default",
        [
            Rule(["", "apps"], ["pods", "deployments"], ["get", "list", "watch"]),
            Rule(["authorization.k8s.io"], ["selfsubjectaccessreviews", "selfsubjectrulesreviews"], ["create"]),
            Rule(["authentication.k8s.io"], ["selfsubjectreviews"], ["create"]),
            Rule(["authorization.k8s.io", "authentication.k8s.io"], ["selfsubjectreviews"], ["create"]),
        ]));
        p.IsReadOnly.Should().BeTrue();
    }

    [Fact]
    public void Create_on_everything_is_not_read_only_even_beside_self_review_rules()
    {
        Perms(("default", [Rule(["*"], ["*"], ["create"])])).IsReadOnly.Should().BeFalse();
        Perms(("default",
        [
            Rule(["authorization.k8s.io"], ["selfsubjectaccessreviews"], ["create"]),
            Rule(["authorization.k8s.io", "apps"], ["deployments"], ["create"]),
        ])).IsReadOnly.Should().BeFalse();
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("patch")]
    [InlineData("delete")]
    [InlineData("deletecollection")]
    [InlineData("*")]
    public void Any_write_verb_makes_it_not_read_only(string verb)
    {
        Perms(("default", [Rule([""], ["pods"], [verb])])).IsReadOnly.Should().BeFalse();
    }

    [Fact]
    public void Incomplete_and_identity_flow_from_facts()
    {
        var p = K8sPermissions.From(new K8sAccessFacts("bob",
            [new K8sRulesReview("default", true, [])], new Dictionary<string, bool>()));
        p.Incomplete.Should().BeTrue();
        p.Identity.Should().Be("bob");
    }

    [Fact]
    public void Missing_lists_ungranted_needs_formatted_and_distinct()
    {
        var p = Perms(("default", [Rule(["apps"], ["deployments"], ["patch"])]));
        var missing = p.Missing([
            ("apps", "deployments", "patch"),
            ("apps", "deployments/scale", "patch"),
            ("apps", "deployments", "delete"),
            ("apps", "deployments", "delete")]);
        missing.Should().Equal("deployments/scale · patch", "deployments · delete");
    }

    [Fact]
    public void All_grants_everything()
    {
        var p = K8sPermissions.All;
        p.Can("x", "y", "z", "ns").Should().BeTrue();
        p.CanCluster("whatever").Should().BeTrue();
        p.IsReadOnly.Should().BeFalse();
        p.Identity.Should().Be("unknown");
        p.Missing(K8sPermissions.WorkloadActions).Should().BeEmpty();
    }

    [Fact]
    public void Page_need_sets_are_defined()
    {
        K8sPermissions.WorkloadActions.Should().HaveCount(10);
        K8sPermissions.PodActions.Should().Equal([("", "pods", "delete")]);
        K8sPermissions.ConfigActions.Should().Equal([("", "configmaps", "update")]);
    }
}
