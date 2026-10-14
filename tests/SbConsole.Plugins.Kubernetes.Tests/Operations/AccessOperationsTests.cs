using FluentAssertions;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Plugins.Kubernetes.Tests.Infrastructure;

namespace SbConsole.Plugins.Kubernetes.Tests.Operations;

[Collection("k3s")]
public class AccessOperationsTests(K3sFixture k3s)
{
    [DockerFact]
    public async Task Test_connection_reports_version_and_identity()
    {
        var result = await k3s.Operations.TestConnectionAsync(k3s.AdminSecret);

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.Identity.Should().MatchRegex(@"^v1\.30\.\d+\+k3s\d+ · as system:admin$");
    }

    [DockerFact]
    public async Task Test_connection_with_a_bad_token_fails_with_the_401_sentence()
    {
        var result = await k3s.Operations.TestConnectionAsync(k3s.TokenSecret("not-a-real-token"));

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("The cluster rejected the credentials, or they have expired.");
    }

    [DockerFact]
    public async Task Test_connection_as_viewer_reports_the_service_account()
    {
        var result = await k3s.Operations.TestConnectionAsync(await k3s.ViewerSecretAsync());

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.Identity.Should().EndWith(" · as system:serviceaccount:sbc-test:viewer");
    }

    [DockerFact]
    public async Task Lists_namespaces_with_pod_counts()
    {
        var namespaces = await k3s.Operations.ListNamespacesAsync(k3s.AdminSecret);

        namespaces.Select(n => n.Name).Should().Contain(["default", "kube-system"]).And.BeInAscendingOrder(StringComparer.Ordinal);
        namespaces.Single(n => n.Name == "kube-system").PodCount.Should().BeGreaterThan(0);
    }

    [DockerFact]
    public async Task Admin_access_allows_scale_and_node_patch()
    {
        var facts = await k3s.Operations.GetAccessAsync(k3s.AdminSecret, []);

        facts.Identity.Should().Be("system:admin");
        facts.Reviews.Select(r => r.Namespace).Should().Equal("default");
        facts.Reviews[0].Rules.Should().Contain(r =>
            (r.Resources.Contains("*") || r.Resources.Contains("deployments/scale"))
            && (r.Verbs.Contains("*") || r.Verbs.Contains("patch")));
        facts.ClusterChecks.Should().BeEquivalentTo(new Dictionary<string, bool>
        {
            ["nodes/patch"] = true, ["pods/eviction/create"] = true, ["nodes/list"] = true, ["pods/list-all"] = true,
        });
    }

    [DockerFact]
    public async Task Viewer_access_has_list_but_not_patch()
    {
        var facts = await k3s.Operations.GetAccessAsync(await k3s.ViewerSecretAsync(), [K3sFixture.TestNamespace, "default"]);

        facts.Identity.Should().Be("system:serviceaccount:sbc-test:viewer");
        facts.Reviews.Select(r => r.Namespace).Should().Equal(K3sFixture.TestNamespace, "default");
        var rules = facts.Reviews[0].Rules;
        rules.Should().Contain(r => r.Resources.Contains("deployments") && r.Verbs.Contains("list"));
        rules.Should().NotContain(r => r.Resources.Contains("deployments") && (r.Verbs.Contains("patch") || r.Verbs.Contains("*")));
        facts.ClusterChecks["nodes/patch"].Should().BeFalse();
        facts.ClusterChecks["pods/eviction/create"].Should().BeFalse();
        facts.ClusterChecks["nodes/list"].Should().BeFalse();
        facts.ClusterChecks["pods/list-all"].Should().BeTrue();
    }
}
