using FluentAssertions;
using k8s;
using k8s.Autorest;
using k8s.Models;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Plugins.Kubernetes.Tests.Infrastructure;

namespace SbConsole.Plugins.Kubernetes.Tests.Operations;

[Collection("k3s")]
public class DeploymentOperationsTests(K3sFixture k3s)
{
    private const string Ns = "sbc-deploy";
    private const string GoodImage = "nginx:1.27-alpine";
    private const string BadImage = "nginx:sbc-does-not-exist";

    private static V1Deployment Deployment(string name, string image, string changeCause) => new()
    {
        Metadata = new V1ObjectMeta
        {
            Name = name, NamespaceProperty = Ns, Labels = new Dictionary<string, string> { ["app"] = name },
            Annotations = new Dictionary<string, string> { ["kubernetes.io/change-cause"] = changeCause },
        },
        Spec = new V1DeploymentSpec
        {
            Replicas = 1,
            Selector = new V1LabelSelector { MatchLabels = new Dictionary<string, string> { ["app"] = name } },
            Template = new V1PodTemplateSpec
            {
                Metadata = new V1ObjectMeta { Labels = new Dictionary<string, string> { ["app"] = name, ["tier"] = "web" } },
                Spec = new V1PodSpec
                {
                    Containers =
                    [
                        new V1Container
                        {
                            Name = "nginx", Image = image,
                            Resources = new V1ResourceRequirements
                            {
                                Requests = new Dictionary<string, ResourceQuantity> { ["cpu"] = new("100m"), ["memory"] = new("64Mi") },
                                Limits = new Dictionary<string, ResourceQuantity> { ["memory"] = new("128Mi") },
                            },
                        },
                    ],
                },
            },
        },
    };

    private static string? RevisionOf(V1Deployment d) =>
        d.Metadata.Annotations is { } a && a.TryGetValue("deployment.kubernetes.io/revision", out var r) ? r : null;

    private async Task TwoRevisionsAsync(string name)
    {
        await k3s.ApplyAsync(Deployment(name, GoodImage, "first"));
        await K3sFixture.WaitAsync(async () =>
            RevisionOf(await k3s.Admin.AppsV1.ReadNamespacedDeploymentAsync(name, Ns)) == "1", TimeSpan.FromMinutes(1));
        await k3s.ApplyAsync(Deployment(name, BadImage, "second"));
        await K3sFixture.WaitAsync(async () =>
            RevisionOf(await k3s.Admin.AppsV1.ReadNamespacedDeploymentAsync(name, Ns)) == "2", TimeSpan.FromMinutes(1));
    }

    private async Task CleanupAsync(string name)
    {
        try
        {
            await k3s.Admin.AppsV1.DeleteNamespacedDeploymentAsync(name, Ns, propagationPolicy: "Background");
        }
        catch (HttpOperationException ex) when ((int)ex.Response.StatusCode == 404)
        {
            // Already gone.
        }

        try
        {
            await k3s.Admin.CoreV1.DeleteNamespacedServiceAsync(name, Ns);
        }
        catch (HttpOperationException ex) when ((int)ex.Response.StatusCode == 404)
        {
            // No service.
        }
    }

    [DockerFact]
    public async Task Detail_lists_both_revisions_their_pods_and_the_matching_service()
    {
        const string name = "pg-detail";
        await TwoRevisionsAsync(name);
        await k3s.Admin.CoreV1.CreateNamespacedServiceAsync(new V1Service
        {
            Metadata = new V1ObjectMeta { Name = name },
            Spec = new V1ServiceSpec
            {
                Selector = new Dictionary<string, string> { ["app"] = name },
                Ports = [new V1ServicePort { Port = 80 }],
            },
        }, Ns);
        await k3s.Admin.CoreV1.CreateNamespacedServiceAsync(new V1Service
        {
            Metadata = new V1ObjectMeta { Name = name + "-other" },
            Spec = new V1ServiceSpec
            {
                Selector = new Dictionary<string, string> { ["app"] = "someone-else" },
                Ports = [new V1ServicePort { Port = 80 }],
            },
        }, Ns);
        try
        {
            K8sDeploymentDetail? detail = null;
            await K3sFixture.WaitAsync(async () =>
            {
                detail = await k3s.Operations.GetDeploymentDetailAsync(k3s.AdminSecret, Ns, name);
                return detail!.Pods.Select(p => p.ReplicaSet).Distinct().Count() == 2;
            }, TimeSpan.FromMinutes(2));

            detail!.Deployment.Name.Should().Be(name);
            detail.CurrentRevision.Should().Be(2);
            detail.Strategy.Should().Be("RollingUpdate");
            detail.MaxSurge.Should().Be("25%");
            detail.MaxUnavailable.Should().Be("25%");
            detail.Selector.Should().Be($"app={name}");
            detail.RevisionHistoryLimit.Should().Be(10);
            detail.PodResources.Should().Be(new K8sResources(100, 64L * 1024 * 1024, null, 128L * 1024 * 1024));
            detail.HpaManaged.Should().BeFalse();
            detail.ReplicaSets.Select(r => r.Revision).Should().BeEquivalentTo([1L, 2L]);
            var current = detail.ReplicaSets.Single(r => r.IsCurrent);
            current.Revision.Should().Be(2);
            current.Images.Should().Equal(BadImage);
            current.ChangeCause.Should().Be("second");
            current.HasTemplate.Should().BeTrue();
            detail.ReplicaSets.Single(r => !r.IsCurrent).Images.Should().Equal(GoodImage);
            detail.Pods.Should().OnlyContain(p => p.Namespace == Ns && detail.ReplicaSets.Any(r => r.Name == p.ReplicaSet));
            detail.Services.Select(s => s.Name).Should().Equal(name);
        }
        finally
        {
            await CleanupAsync(name);
            await k3s.Admin.CoreV1.DeleteNamespacedServiceAsync(name + "-other", Ns);
        }
    }

    [DockerFact]
    public async Task Detail_of_a_missing_deployment_is_null()
    {
        await k3s.EnsureNamespaceAsync(Ns);

        var detail = await k3s.Operations.GetDeploymentDetailAsync(k3s.AdminSecret, Ns, "nope");

        detail.Should().BeNull();
    }

    [DockerFact]
    public async Task Rollback_restores_the_revision_template_and_returns_the_previous_revision()
    {
        const string name = "pg-rollback";
        await TwoRevisionsAsync(name);
        try
        {
            var from = await k3s.Operations.RollbackDeploymentAsync(k3s.AdminSecret, Ns, name, 1);

            from.Should().Be(2);
            var d = await k3s.Admin.AppsV1.ReadNamespacedDeploymentAsync(name, Ns);
            d.Spec.Template.Spec.Containers.Single().Image.Should().Be(GoodImage);
            d.Spec.Template.Metadata.Labels.Should().NotContainKey("pod-template-hash");
            d.Spec.Template.Metadata.Labels.Should().Contain("tier", "web");
            await K3sFixture.WaitAsync(async () =>
                RevisionOf(await k3s.Admin.AppsV1.ReadNamespacedDeploymentAsync(name, Ns)) == "3", TimeSpan.FromMinutes(1));
        }
        finally
        {
            await CleanupAsync(name);
        }
    }

    [DockerFact]
    public async Task Rollback_to_a_missing_revision_throws_a_plain_message()
    {
        const string name = "pg-rollback-missing";
        await TwoRevisionsAsync(name);
        try
        {
            var act = () => k3s.Operations.RollbackDeploymentAsync(k3s.AdminSecret, Ns, name, 7);

            (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("Revision 7 no longer exists.");
        }
        finally
        {
            await CleanupAsync(name);
        }
    }
}
