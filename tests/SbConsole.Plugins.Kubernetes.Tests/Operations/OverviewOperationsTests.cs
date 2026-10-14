using FluentAssertions;
using k8s;
using k8s.Models;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Plugins.Kubernetes.Tests.Infrastructure;

namespace SbConsole.Plugins.Kubernetes.Tests.Operations;

[Collection("k3s")]
public class OverviewOperationsTests(K3sFixture k3s)
{
    private const string CrashNamespace = "sbc-overview-crash";
    private const string ScopedNamespace = "sbc-overview-scoped";

    [DockerFact]
    public async Task Overview_against_k3s_counts_nodes_and_pods()
    {
        await WaitForKubeSystemAsync();

        var overview = await k3s.Operations.GetOverviewAsync(k3s.AdminSecret, []);

        overview.Nodes.Should().ContainSingle();
        overview.Nodes[0].ReadyStatus.Should().Be("True");
        overview.Nodes[0].CpuAllocatableMilli.Should().BeGreaterThan(0);
        overview.Nodes[0].MemAllocatableBytes.Should().BeGreaterThan(0);
        overview.Nodes[0].PodCount.Should().BeGreaterThan(0);
        overview.Pods.Running.Should().BeGreaterThan(0);
        overview.Cluster.Provider.Should().Be("k3s");
        overview.Cluster.ServerVersion.Should().MatchRegex(@"^v1\.30\.\d+\+k3s\d+$");
        overview.Cluster.Identity.Should().Be("system:admin");
        overview.Cluster.NamespaceCount.Should().BeGreaterThanOrEqualTo(4);
        overview.Cluster.ServerUrl.Should().StartWith("https://");
        if (overview.CpuUsageFraction is { } usage)
        {
            usage.Should().BeInRange(0, 10, "usage comes from metrics-server only when it answers");
        }
    }

    [DockerFact]
    public async Task Overview_scoped_to_a_namespace_only_counts_its_pods()
    {
        await k3s.EnsureNamespaceAsync(ScopedNamespace);
        await CreatePodAsync(ScopedNamespace, "sleeper", ["sh", "-c", "sleep 3600"], "Always");
        await K3sFixture.WaitAsync(async () =>
            (await k3s.Admin.CoreV1.ReadNamespacedPodAsync("sleeper", ScopedNamespace)).Status?.Phase == "Running",
            TimeSpan.FromMinutes(3));

        var overview = await k3s.Operations.GetOverviewAsync(k3s.AdminSecret, [ScopedNamespace]);

        overview.Pods.Should().Be(new K8sPodCounts(1, 0, 0, 0));
        overview.Nodes.Should().ContainSingle("nodes are cluster-wide even when namespaces are selected");
        overview.ProblemPods.Should().BeEmpty();
    }

    [DockerFact]
    public async Task Crash_looping_pod_is_reported()
    {
        await k3s.EnsureNamespaceAsync(CrashNamespace);
        await CreatePodAsync(CrashNamespace, "crasher", ["sh", "-c", "exit 1"], "Always");
        await K3sFixture.WaitAsync(async () =>
        {
            var pod = await k3s.Admin.CoreV1.ReadNamespacedPodAsync("crasher", CrashNamespace);
            return pod.Status?.ContainerStatuses?.Any(c => c.State?.Waiting?.Reason == "CrashLoopBackOff") == true;
        }, TimeSpan.FromMinutes(3));

        var overview = await k3s.Operations.GetOverviewAsync(k3s.AdminSecret, [CrashNamespace]);

        var pod = overview.ProblemPods.Should().ContainSingle(p => p.Name == "crasher").Subject;
        pod.Status.Should().Be("CrashLoopBackOff");
        pod.Restarts.Should().BeGreaterThan(0);
        pod.LastExitCode.Should().Be(1);
        pod.LastTerminationReason.Should().Be("Error");
    }

    [DockerFact]
    public async Task Viewer_without_node_access_still_gets_an_overview_with_no_nodes()
    {
        await WaitForKubeSystemAsync();

        var overview = await k3s.Operations.GetOverviewAsync(await k3s.ViewerSecretAsync(), ["kube-system"]);

        overview.Nodes.Should().BeEmpty();
        overview.Pods.Running.Should().BeGreaterThan(0);
        overview.CpuUsageFraction.Should().BeNull();
        overview.Cluster.Identity.Should().Be("system:serviceaccount:sbc-test:viewer");
    }

    /// <summary>kube-system pods (coredns, local-path, metrics-server) take a moment after the API answers.</summary>
    private Task WaitForKubeSystemAsync() =>
        K3sFixture.WaitAsync(async () =>
        {
            var pods = await k3s.Admin.CoreV1.ListNamespacedPodAsync("kube-system");
            return pods.Items.Any(p => p.Status?.Phase == "Running");
        }, TimeSpan.FromMinutes(2));

    private async Task CreatePodAsync(string ns, string name, IList<string> command, string restartPolicy)
    {
        try
        {
            await k3s.Admin.CoreV1.CreateNamespacedPodAsync(new V1Pod
            {
                Metadata = new V1ObjectMeta { Name = name },
                Spec = new V1PodSpec
                {
                    RestartPolicy = restartPolicy,
                    TerminationGracePeriodSeconds = 0,
                    Containers = [new V1Container { Name = "main", Image = "busybox:1.36", Command = command }],
                },
            }, ns);
        }
        catch (k8s.Autorest.HttpOperationException ex) when ((int)ex.Response.StatusCode == 409)
        {
            // Already created by an earlier run in this fixture.
        }
    }
}
