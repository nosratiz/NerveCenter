using FluentAssertions;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Plugins.Kubernetes.Nodes;
using SbConsole.Plugins.Kubernetes.Tests.Infrastructure;
using SbConsole.Plugins.Kubernetes.Workloads;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes.Tests.Operations;

[Collection("k3s")]
public class DrainOperationsTests(K3sFixture k3s)
{
    private const string Ns = "sbc-drain";
    private const string App = "drainme";

    private static V1Deployment Deployment() => new()
    {
        Metadata = new V1ObjectMeta { Name = App, NamespaceProperty = Ns, Labels = new Dictionary<string, string> { ["app"] = App } },
        Spec = new V1DeploymentSpec
        {
            Replicas = 2,
            Selector = new V1LabelSelector { MatchLabels = new Dictionary<string, string> { ["app"] = App } },
            Template = new V1PodTemplateSpec
            {
                Metadata = new V1ObjectMeta { Labels = new Dictionary<string, string> { ["app"] = App } },
                Spec = new V1PodSpec
                {
                    TerminationGracePeriodSeconds = 0,
                    Containers = [new V1Container { Name = "nginx", Image = "nginx:1.27-alpine" }],
                },
            },
        },
    };

    private static V1PodDisruptionBudget Pdb() => new()
    {
        Metadata = new V1ObjectMeta { Name = App, NamespaceProperty = Ns },
        Spec = new V1PodDisruptionBudgetSpec
        {
            MinAvailable = 2,
            Selector = new V1LabelSelector { MatchLabels = new Dictionary<string, string> { ["app"] = App } },
        },
    };

    private async Task<string> SeedAsync()
    {
        var node = (await k3s.Admin.CoreV1.ListNodeAsync()).Items.Single().Metadata.Name;
        await k3s.ApplyAsync(Deployment());
        try
        {
            await k3s.Admin.PolicyV1.CreateNamespacedPodDisruptionBudgetAsync(Pdb(), Ns);
        }
        catch (HttpOperationException ex) when ((int)ex.Response.StatusCode == 409)
        {
            // Left over from an earlier run in this container.
        }

        await K3sFixture.WaitAsync(async () =>
        {
            var pdb = await k3s.Admin.PolicyV1.ReadNamespacedPodDisruptionBudgetAsync(App, Ns);
            return pdb.Status is { CurrentHealthy: 2, DesiredHealthy: 2, DisruptionsAllowed: 0 };
        }, TimeSpan.FromMinutes(2));
        return node;
    }

    private async Task TeardownAsync(string? node)
    {
        // Other Docker tests schedule pods on this single node: never leave it cordoned.
        if (node is not null)
        {
            await k3s.Operations.SetUnschedulableAsync(k3s.AdminSecret, node, false);
        }

        try
        {
            await k3s.Admin.CoreV1.DeleteNamespaceAsync(Ns);
        }
        catch (HttpOperationException ex) when ((int)ex.Response.StatusCode == 404)
        {
            // Already gone.
        }
    }

    /// <summary>
    /// The real operations, except that the drain inputs keep only this test's namespace: draining the whole single k3s
    /// node would evict coredns, metrics-server and the other tests' pods.
    /// </summary>
    private IKubernetesOperations ScopedOperations()
    {
        var real = k3s.Operations;
        var ops = Substitute.For<IKubernetesOperations>();
        ops.SetUnschedulableAsync(default!, default!, default, default).ReturnsForAnyArgs(ci =>
            real.SetUnschedulableAsync(ci.ArgAt<string>(0), ci.ArgAt<string>(1), ci.ArgAt<bool>(2), ci.ArgAt<CancellationToken>(3)));
        ops.GetDrainInputsAsync(default!, default!, default).ReturnsForAnyArgs(async ci =>
        {
            var inputs = await real.GetDrainInputsAsync(ci.ArgAt<string>(0), ci.ArgAt<string>(1), ci.ArgAt<CancellationToken>(2));
            return inputs with { Pods = inputs.Pods.Where(p => p.Namespace == Ns).ToList() };
        });
        ops.EvictPodAsync(default!, default!, default!, default, default).ReturnsForAnyArgs(ci =>
            real.EvictPodAsync(ci.ArgAt<string>(0), ci.ArgAt<string>(1), ci.ArgAt<string>(2), ci.ArgAt<int>(3), ci.ArgAt<CancellationToken>(4)));
        ops.DeletePodAsync(default!, default!, default!, default, default).ReturnsForAnyArgs(ci =>
            real.DeletePodAsync(ci.ArgAt<string>(0), ci.ArgAt<string>(1), ci.ArgAt<string>(2), ci.ArgAt<int?>(3), ci.ArgAt<CancellationToken>(4)));
        return ops;
    }

    private DrainNodeCommandHandler Handler(IKubernetesOperations ops, Guid id)
    {
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(id, Arg.Any<CancellationToken>()).Returns(k3s.AdminSecret);
        return new DrainNodeCommandHandler(ops, connections, Substitute.For<IAuditScope>(), TimeProvider.System,
            NullLogger<DrainNodeCommandHandler>.Instance);
    }

    [DockerFact]
    public async Task Pdb_blocks_the_drain_until_forced()
    {
        string? node = null;
        try
        {
            node = await SeedAsync();

            // Preview: both replicas are blocked by minAvailable 2.
            var inputs = await k3s.Operations.GetDrainInputsAsync(k3s.AdminSecret, node);
            inputs.Node.Name.Should().Be(node);
            inputs.Pdbs.Should().ContainSingle(p => p.Namespace == Ns && p.Name == App)
                .Which.Should().Match<K8sPdb>(p => p.Rule == "minAvailable 2" && p.DisruptionsAllowed == 0 && p.CurrentHealthy == 2);
            inputs.OwnerReplicas[$"Deployment/{Ns}/{App}"].Should().Be(2);
            var plan = DrainPlanner.Plan(inputs, 0);
            plan.Pods.Where(p => p.Pod.Namespace == Ns).Should().HaveCount(2)
                .And.OnlyContain(p => p.Group == DrainGroup.BlockedByPdb && p.Note == "PDB drainme minAvailable 2 · now 2");

            // The Eviction API answers 429.
            var pod = plan.Pods.First(p => p.Pod.Namespace == Ns).Pod;
            (await k3s.Operations.EvictPodAsync(k3s.AdminSecret, Ns, pod.Name, 0)).Should().Be(K8sEvictionOutcome.BlockedByPdb);
            (await k3s.Operations.EvictPodAsync(k3s.AdminSecret, Ns, "sbc-no-such-pod", 0)).Should().Be(K8sEvictionOutcome.NotFound);

            var id = Guid.NewGuid();
            var target = new K8sTarget(id, "k3s", null, node);
            var handler = Handler(ScopedOperations(), id);
            handler.Deadline = TimeSpan.FromSeconds(20);

            var blocked = await handler.HandleAsync(new DrainCommand(target, new DrainOptions(false, false, 1)), null);

            blocked.IsSuccess.Should().BeTrue();
            blocked.Value!.TimedOut.Should().BeTrue();
            blocked.Value.Pods.Should().HaveCount(2).And.OnlyContain(p => p.Outcome == DrainPodOutcome.Blocked);
            (await k3s.Admin.CoreV1.ReadNodeAsync(node)).Spec.Unschedulable.Should().BeTrue("drain cordons first");

            var forced = await Handler(ScopedOperations(), id)
                .HandleAsync(new DrainCommand(target, new DrainOptions(false, true, 1)), null);

            forced.Value!.Pods.Should().HaveCount(2).And.OnlyContain(p => p.Outcome == DrainPodOutcome.Deleted);
            forced.Value.TimedOut.Should().BeFalse();
        }
        finally
        {
            await TeardownAsync(node);
        }
    }
}
