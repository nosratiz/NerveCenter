using FluentAssertions;
using k8s;
using k8s.Autorest;
using k8s.Models;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Plugins.Kubernetes.Tests.Infrastructure;

namespace SbConsole.Plugins.Kubernetes.Tests.Operations;

[Collection("k3s")]
public class WorkloadOperationsTests(K3sFixture k3s)
{
    private const string Ns = "sbc-wl";

    private async Task DeployAsync(string name, int replicas = 1)
    {
        var labels = new Dictionary<string, string> { ["app"] = name };
        await k3s.ApplyAsync(new V1Deployment
        {
            Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = Ns, Labels = labels },
            Spec = new V1DeploymentSpec
            {
                Replicas = replicas,
                Selector = new V1LabelSelector { MatchLabels = labels },
                Template = new V1PodTemplateSpec
                {
                    Metadata = new V1ObjectMeta { Labels = labels },
                    Spec = new V1PodSpec
                    {
                        Containers =
                        [
                            new V1Container
                            {
                                Name = "nginx",
                                Image = "nginx:1.27-alpine",
                                Resources = new V1ResourceRequirements
                                {
                                    Requests = new Dictionary<string, ResourceQuantity>
                                    {
                                        ["cpu"] = new("100m"), ["memory"] = new("64Mi"),
                                    },
                                },
                            },
                        ],
                    },
                },
            },
        });
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
    }

    [DockerFact]
    public async Task List_filters_by_label_selector_and_counts_by_kind()
    {
        await DeployAsync("web");
        try
        {
            var found = await k3s.Operations.ListWorkloadsAsync(k3s.AdminSecret, K8sWorkloadKind.Deployment, [Ns], "app=web");
            var other = await k3s.Operations.ListWorkloadsAsync(k3s.AdminSecret, K8sWorkloadKind.Deployment, [Ns], "app=other");
            var elsewhere = await k3s.Operations.ListWorkloadsAsync(k3s.AdminSecret, K8sWorkloadKind.Deployment, ["kube-system"], "app=web");
            var statefulSets = await k3s.Operations.ListWorkloadsAsync(k3s.AdminSecret, K8sWorkloadKind.StatefulSet, [Ns], null);
            var counts = await k3s.Operations.CountWorkloadsAsync(k3s.AdminSecret, [Ns]);

            var web = found.Should().ContainSingle().Subject;
            web.Kind.Should().Be(K8sWorkloadKind.Deployment);
            web.Namespace.Should().Be(Ns);
            web.Name.Should().Be("web");
            web.Desired.Should().Be(1);
            web.Images.Should().Equal("nginx:1.27-alpine");
            other.Should().BeEmpty();
            elsewhere.Should().BeEmpty();
            statefulSets.Should().BeEmpty();
            counts.Keys.Should().BeEquivalentTo(Enum.GetValues<K8sWorkloadKind>());
            counts[K8sWorkloadKind.Deployment].Count.Should().Be(1);
            counts[K8sWorkloadKind.Job].Should().Be(new K8sKindCount(0, false));
        }
        finally
        {
            await CleanupAsync("web");
        }
    }

    [DockerFact]
    public async Task Scale_returns_the_previous_replicas_and_the_status_follows()
    {
        await DeployAsync("web-scale");
        try
        {
            var info = await k3s.Operations.GetScaleInfoAsync(k3s.AdminSecret, K8sWorkloadKind.Deployment, Ns, "web-scale");
            info.Replicas.Should().Be(1);
            info.HpaManaged.Should().BeFalse();
            info.HpaName.Should().BeNull();
            info.PerPodCpuMilli.Should().Be(100);
            info.PerPodMemBytes.Should().Be(64L * 1024 * 1024);

            var previous = await k3s.Operations.ScaleAsync(k3s.AdminSecret, K8sWorkloadKind.Deployment, Ns, "web-scale", 2);

            previous.Should().Be(1);
            await K3sFixture.WaitAsync(async () =>
                (await k3s.Admin.AppsV1.ReadNamespacedDeploymentAsync("web-scale", Ns)).Status?.Replicas == 2,
                TimeSpan.FromMinutes(2));
        }
        finally
        {
            await CleanupAsync("web-scale");
        }
    }

    [DockerFact]
    public async Task Restart_sets_the_restarted_at_annotation_on_the_template()
    {
        await DeployAsync("web-restart");
        try
        {
            var at = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

            await k3s.Operations.RestartAsync(k3s.AdminSecret, K8sWorkloadKind.Deployment, Ns, "web-restart", at);

            var d = await k3s.Admin.AppsV1.ReadNamespacedDeploymentAsync("web-restart", Ns);
            d.Spec.Template.Metadata.Annotations.Should()
                .Contain("kubectl.kubernetes.io/restartedAt", "2026-10-14T12:00:00.0000000Z");
        }
        finally
        {
            await CleanupAsync("web-restart");
        }
    }

    [DockerFact]
    public async Task An_hpa_targeting_the_deployment_marks_it_hpa_managed()
    {
        await DeployAsync("web-hpa");
        try
        {
            await k3s.Admin.AutoscalingV2.CreateNamespacedHorizontalPodAutoscalerAsync(new V2HorizontalPodAutoscaler
            {
                Metadata = new V1ObjectMeta { Name = "web-hpa-auto", NamespaceProperty = Ns },
                Spec = new V2HorizontalPodAutoscalerSpec
                {
                    MinReplicas = 1,
                    MaxReplicas = 3,
                    ScaleTargetRef = new V2CrossVersionObjectReference { ApiVersion = "apps/v1", Kind = "Deployment", Name = "web-hpa" },
                },
            }, Ns);

            var info = await k3s.Operations.GetScaleInfoAsync(k3s.AdminSecret, K8sWorkloadKind.Deployment, Ns, "web-hpa");

            info.HpaManaged.Should().BeTrue();
            info.HpaName.Should().Be("web-hpa-auto");
        }
        finally
        {
            await k3s.Admin.AutoscalingV2.DeleteNamespacedHorizontalPodAutoscalerAsync("web-hpa-auto", Ns);
            await CleanupAsync("web-hpa");
        }
    }

    [DockerFact]
    public async Task Yaml_has_no_managed_fields_and_a_missing_resource_is_null()
    {
        await DeployAsync("web-yaml");
        try
        {
            var yaml = await k3s.Operations.GetYamlAsync(k3s.AdminSecret, K8sResourceKind.Deployment, Ns, "web-yaml");
            var missing = await k3s.Operations.GetYamlAsync(k3s.AdminSecret, K8sResourceKind.Pod, Ns, "no-such-pod");
            var nodes = await k3s.Admin.CoreV1.ListNodeAsync();
            var node = await k3s.Operations.GetYamlAsync(k3s.AdminSecret, K8sResourceKind.Node, null, nodes.Items[0].Metadata.Name);

            yaml.Should().Contain("name: web-yaml").And.Contain("nginx:1.27-alpine").And.NotContain("managedFields");
            missing.Should().BeNull();
            node.Should().Contain(nodes.Items[0].Metadata.Name).And.NotContain("managedFields");
        }
        finally
        {
            await CleanupAsync("web-yaml");
        }
    }

    [DockerFact]
    public async Task Delete_removes_the_deployment()
    {
        await DeployAsync("web-delete");

        await k3s.Operations.DeleteWorkloadAsync(k3s.AdminSecret, K8sWorkloadKind.Deployment, Ns, "web-delete");

        await K3sFixture.WaitAsync(async () =>
        {
            try
            {
                await k3s.Admin.AppsV1.ReadNamespacedDeploymentAsync("web-delete", Ns);
                return false;
            }
            catch (HttpOperationException ex) when ((int)ex.Response.StatusCode == 404)
            {
                return true;
            }
        }, TimeSpan.FromMinutes(1));
    }
}
