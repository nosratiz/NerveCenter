using FluentAssertions;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Plugins.Kubernetes.Workloads;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes.Tests.Workloads;

public class WorkloadConfirmationsTests
{
    private static ConnectionInfo Connection(params string[] tags) => new(Guid.NewGuid(), "aks-prod", "kubernetes", tags);

    [Fact]
    public void Delete_on_a_prod_connection_is_a_destructive_prod_request_with_details()
    {
        var request = WorkloadConfirmations.Delete(Connection("prod"), K8sWorkloadKind.Deployment, "checkout", "api", 2, 3, ["api:1", "sidecar:2"]);

        request.Should().Be(new ConfirmationRequest("Delete Deployment", "api", true,
            Consequence: "Deletes the Deployment and, in the background, everything it owns (ReplicaSets and Pods).",
            Details: request.Details,
            AuditAction: "k8s.deployment.delete",
            Destructive: true));
        request.Details.Should().Equal(
            new ConfirmationDetail("Namespace", "checkout"), new ConfirmationDetail("Ready", "2/3"), new ConfirmationDetail("Images", "api:1 · sidecar:2"));
    }

    [Fact]
    public void Delete_on_a_non_prod_connection_is_destructive_but_not_prod()
    {
        var request = WorkloadConfirmations.Delete(Connection("staging"), K8sWorkloadKind.Job, "batch", "nightly", 0, 1, ["etl:4"]);

        request.IsProd.Should().BeFalse();
        request.Destructive.Should().BeTrue();
        request.AuditAction.Should().Be("k8s.job.delete");
    }

    [Theory]
    [InlineData(K8sWorkloadKind.Deployment, "Deletes the Deployment and, in the background, everything it owns (ReplicaSets and Pods).")]
    [InlineData(K8sWorkloadKind.StatefulSet, "Deletes the StatefulSet and, in the background, everything it owns (Pods).")]
    [InlineData(K8sWorkloadKind.DaemonSet, "Deletes the DaemonSet and, in the background, everything it owns (Pods).")]
    [InlineData(K8sWorkloadKind.Job, "Deletes the Job and, in the background, everything it owns (Pods).")]
    [InlineData(K8sWorkloadKind.CronJob, "Deletes the CronJob and, in the background, everything it owns (Jobs and Pods).")]
    public void Delete_names_what_each_kind_owns(K8sWorkloadKind kind, string consequence)
    {
        var request = WorkloadConfirmations.Delete(Connection("prod"), kind, "ns", "x", 1, 1, []);

        request.Verb.Should().Be($"Delete {kind}");
        request.Consequence.Should().Be(consequence);
    }
}
