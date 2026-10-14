using System.Net;
using FluentAssertions;
using k8s;
using k8s.Autorest;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Plugins.Kubernetes.Tests.Infrastructure;

namespace SbConsole.Plugins.Kubernetes.Tests.Operations;

[Collection("k3s")]
public class NodeOperationsTests(K3sFixture k3s)
{
    [DockerFact]
    public async Task List_nodes_shows_the_k3s_node_ready_with_allocatable_and_requests()
    {
        await WaitForScheduledPodsAsync();
        var name = await NodeNameAsync();

        var nodes = await k3s.Operations.ListNodesAsync(k3s.AdminSecret);

        var node = nodes.Should().ContainSingle().Subject;
        node.Name.Should().Be(name);
        node.ReadyStatus.Should().Be("True");
        node.CpuAllocatableMilli.Should().BeGreaterThan(0);
        node.MemAllocatableBytes.Should().BeGreaterThan(0);
        node.CpuRequestedMilli.Should().BeGreaterThan(0, "coredns and metrics-server request CPU");
        node.MemRequestedBytes.Should().BeGreaterThan(0);
        node.PodCount.Should().BeGreaterThan(0);
        node.KubeletVersion.Should().StartWith("v1.30");
    }

    [DockerFact]
    public async Task Pods_on_the_node_are_listed()
    {
        await WaitForScheduledPodsAsync();
        var name = await NodeNameAsync();

        var pods = await k3s.Operations.ListPodsOnNodeAsync(k3s.AdminSecret, name);

        pods.Should().NotBeEmpty();
        pods.Should().OnlyContain(p => p.NodeName == name);
        pods.Should().Contain(p => p.Namespace == "kube-system");
    }

    [DockerFact]
    public async Task Cordon_sets_unschedulable_and_uncordon_clears_it()
    {
        var name = await NodeNameAsync();
        try
        {
            await k3s.Operations.SetUnschedulableAsync(k3s.AdminSecret, name, true);
            (await k3s.Admin.CoreV1.ReadNodeAsync(name)).Spec.Unschedulable.Should().BeTrue();
            (await k3s.Operations.ListNodesAsync(k3s.AdminSecret)).Single().Unschedulable.Should().BeTrue();

            await k3s.Operations.SetUnschedulableAsync(k3s.AdminSecret, name, false);
            (await k3s.Admin.CoreV1.ReadNodeAsync(name)).Spec.Unschedulable.Should().NotBe(true);
            (await k3s.Operations.ListNodesAsync(k3s.AdminSecret)).Single().Unschedulable.Should().BeFalse();
        }
        finally
        {
            // Other tests schedule pods on this single node: never leave it cordoned.
            await k3s.Operations.SetUnschedulableAsync(k3s.AdminSecret, name, false);
        }
    }

    [DockerFact]
    public async Task Viewer_listing_nodes_is_forbidden()
    {
        var viewer = await k3s.ViewerSecretAsync();

        var act = () => k3s.Operations.ListNodesAsync(viewer);

        var ex = (await act.Should().ThrowAsync<HttpOperationException>()).Which;
        ex.Response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        FriendlyKubernetesError.From(ex).Should().Be("Your account can't list nodes.");
    }

    private async Task<string> NodeNameAsync() => (await k3s.Admin.CoreV1.ListNodeAsync()).Items.Single().Metadata.Name;

    /// <summary>kube-system pods take a moment to be scheduled after the API answers.</summary>
    private Task WaitForScheduledPodsAsync() =>
        K3sFixture.WaitAsync(async () =>
        {
            var pods = await k3s.Admin.CoreV1.ListNamespacedPodAsync("kube-system");
            return pods.Items.Any(p => p.Status?.Phase == "Running" && !string.IsNullOrEmpty(p.Spec?.NodeName));
        }, TimeSpan.FromMinutes(2));
}
