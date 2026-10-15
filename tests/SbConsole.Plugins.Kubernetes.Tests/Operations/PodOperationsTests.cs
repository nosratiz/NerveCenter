using System.Text.Json;
using FluentAssertions;
using k8s;
using k8s.Autorest;
using k8s.Models;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Plugins.Kubernetes.Tests.Infrastructure;

namespace SbConsole.Plugins.Kubernetes.Tests.Operations;

[Collection("k3s")]
public class PodOperationsTests(K3sFixture k3s)
{
    private const string Ns = "sbc-pods";
    private const string Image = "nginx:1.27-alpine";
    private const string SecretPlaintext = "sbc-plain-xyz-123";

    private static V1Pod EnvPod(string name) => new()
    {
        Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = Ns },
        Spec = new V1PodSpec
        {
            TerminationGracePeriodSeconds = 0,
            Containers =
            [
                new V1Container
                {
                    Name = "app", Image = Image,
                    Env =
                    [
                        new V1EnvVar { Name = "MODE", Value = "prod" },
                        new V1EnvVar { Name = "DB_HOST", ValueFrom = new V1EnvVarSource { ConfigMapKeyRef = new V1ConfigMapKeySelector { Name = name + "-cm", Key = "db.host" } } },
                        new V1EnvVar { Name = "DB_PASSWORD", ValueFrom = new V1EnvVarSource { SecretKeyRef = new V1SecretKeySelector { Name = name + "-secret", Key = "password" } } },
                        new V1EnvVar { Name = "POD_NAME", ValueFrom = new V1EnvVarSource { FieldRef = new V1ObjectFieldSelector { FieldPath = "metadata.name" } } },
                    ],
                    EnvFrom =
                    [
                        new V1EnvFromSource { Prefix = "CFG_", ConfigMapRef = new V1ConfigMapEnvSource { Name = name + "-cm" } },
                        new V1EnvFromSource { Prefix = "SEC_", SecretRef = new V1SecretEnvSource { Name = name + "-secret" } },
                    ],
                },
            ],
            Volumes = [new V1Volume { Name = "creds", Secret = new V1SecretVolumeSource { SecretName = name + "-secret" } }],
        },
    };

    private async Task SeedEnvPodAsync(string name)
    {
        await k3s.EnsureNamespaceAsync(Ns);
        await k3s.Admin.CoreV1.CreateNamespacedConfigMapAsync(new V1ConfigMap
        {
            Metadata = new V1ObjectMeta { Name = name + "-cm" },
            Data = new Dictionary<string, string> { ["db.host"] = "pg.internal" },
        }, Ns);
        await k3s.Admin.CoreV1.CreateNamespacedSecretAsync(new V1Secret
        {
            Metadata = new V1ObjectMeta { Name = name + "-secret" },
            StringData = new Dictionary<string, string> { ["password"] = SecretPlaintext },
        }, Ns);
        await k3s.Admin.CoreV1.CreateNamespacedPodAsync(EnvPod(name), Ns);
    }

    private async Task CleanupAsync(string name)
    {
        await Ignore404(() => k3s.Admin.CoreV1.DeleteNamespacedPodAsync(name, Ns, gracePeriodSeconds: 0));
        await Ignore404(() => k3s.Admin.CoreV1.DeleteNamespacedConfigMapAsync(name + "-cm", Ns));
        await Ignore404(() => k3s.Admin.CoreV1.DeleteNamespacedSecretAsync(name + "-secret", Ns));
    }

    private static async Task Ignore404(Func<Task> delete)
    {
        try
        {
            await delete();
        }
        catch (HttpOperationException ex) when ((int)ex.Response.StatusCode == 404)
        {
            // Already gone.
        }
    }

    [DockerFact]
    public async Task Detail_resolves_config_map_env_and_never_a_secret_value()
    {
        const string name = "env-pod";
        await SeedEnvPodAsync(name);
        try
        {
            var detail = await k3s.Operations.GetPodDetailAsync(k3s.AdminSecret, Ns, name);

            detail.Should().NotBeNull();
            detail!.Pod.Name.Should().Be(name);
            detail.Env.Should().Contain(new K8sEnvVar("app", "MODE", "prod", K8sEnvSource.Literal, null));
            detail.Env.Should().Contain(new K8sEnvVar("app", "DB_HOST", "pg.internal", K8sEnvSource.ConfigMap, name + "-cm"));
            detail.Env.Should().Contain(new K8sEnvVar("app", "CFG_db.host", "pg.internal", K8sEnvSource.ConfigMap, name + "-cm"));
            detail.Env.Should().Contain(new K8sEnvVar("app", "DB_PASSWORD", null, K8sEnvSource.Secret, name + "-secret"));
            detail.Env.Should().Contain(new K8sEnvVar("app", "SEC_*", null, K8sEnvSource.Secret, name + "-secret"));
            detail.Env.Should().Contain(new K8sEnvVar("app", "POD_NAME", name, K8sEnvSource.FieldRef, "metadata.name"));
            detail.OwnerChain.Should().BeEmpty();
            detail.VolumeCount.Should().BeGreaterThanOrEqualTo(1);
            detail.SecretMountCount.Should().BeGreaterThanOrEqualTo(1);
            detail.QosClass.Should().Be("BestEffort");
            detail.Containers.Select(c => c.Name).Should().Equal("app");
            detail.DescribeYaml.Should().Contain($"name: {name}").And.NotContain("managedFields");
        }
        finally
        {
            await CleanupAsync(name);
        }
    }

    [DockerFact]
    public async Task Secret_value_never_appears_in_pod_detail()
    {
        const string name = "secret-pod";
        await SeedEnvPodAsync(name);
        try
        {
            var detail = await k3s.Operations.GetPodDetailAsync(k3s.AdminSecret, Ns, name);

            var json = JsonSerializer.Serialize(detail);
            json.Should().Contain(name + "-secret");
            json.Should().NotContain(SecretPlaintext);
            json.Should().NotContain(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(SecretPlaintext)));
        }
        finally
        {
            await CleanupAsync(name);
        }
    }

    [DockerFact]
    public async Task Detail_has_the_node_and_its_memory_request_fraction()
    {
        const string name = "node-pod";
        await SeedEnvPodAsync(name);
        try
        {
            K8sPodDetail? detail = null;
            await K3sFixture.WaitAsync(async () =>
            {
                detail = await k3s.Operations.GetPodDetailAsync(k3s.AdminSecret, Ns, name);
                return detail?.Node is not null;
            }, TimeSpan.FromMinutes(1));

            detail!.Node!.Name.Should().Be(detail.Pod.NodeName);
            detail.Node.ReadyStatus.Should().Be("True");
            detail.Node.MemoryRequestFraction.Should().NotBeNull().And.BeInRange(0, 1);
        }
        finally
        {
            await CleanupAsync(name);
        }
    }

    [DockerFact]
    public async Task Viewer_gets_the_detail_without_the_node()
    {
        const string name = "viewer-pod";
        var viewer = await k3s.ViewerSecretAsync();
        await SeedEnvPodAsync(name);
        try
        {
            K8sPodDetail? detail = null;
            await K3sFixture.WaitAsync(async () =>
            {
                detail = await k3s.Operations.GetPodDetailAsync(viewer, Ns, name);
                return detail?.Pod.NodeName is not null;
            }, TimeSpan.FromMinutes(1));

            detail!.Node.Should().BeNull();
            detail.Env.Should().Contain(new K8sEnvVar("app", "DB_HOST", "pg.internal", K8sEnvSource.ConfigMap, name + "-cm"));
            detail.Env.Should().Contain(new K8sEnvVar("app", "DB_PASSWORD", null, K8sEnvSource.Secret, name + "-secret"));
        }
        finally
        {
            await CleanupAsync(name);
        }
    }

    [DockerFact]
    public async Task Deployment_pod_has_the_replica_set_and_deployment_owner_chain()
    {
        const string name = "chain";
        await k3s.ApplyAsync(new V1Deployment
        {
            Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = Ns },
            Spec = new V1DeploymentSpec
            {
                Replicas = 1,
                Selector = new V1LabelSelector { MatchLabels = new Dictionary<string, string> { ["app"] = name } },
                Template = new V1PodTemplateSpec
                {
                    Metadata = new V1ObjectMeta { Labels = new Dictionary<string, string> { ["app"] = name } },
                    Spec = new V1PodSpec { Containers = [new V1Container { Name = "nginx", Image = Image }] },
                },
            },
        });
        try
        {
            string? podName = null;
            await K3sFixture.WaitAsync(async () =>
            {
                var pods = await k3s.Admin.CoreV1.ListNamespacedPodAsync(Ns, labelSelector: $"app={name}");
                podName = pods.Items.FirstOrDefault()?.Metadata.Name;
                return podName is not null;
            }, TimeSpan.FromMinutes(1));

            var detail = await k3s.Operations.GetPodDetailAsync(k3s.AdminSecret, Ns, podName!);

            detail!.OwnerChain.Should().HaveCount(2);
            detail.OwnerChain[0].Kind.Should().Be("ReplicaSet");
            detail.OwnerChain[0].Revision.Should().Be(1);
            detail.OwnerChain[1].Should().Be(new K8sOwnerLink("Deployment", name, null));
        }
        finally
        {
            await Ignore404(() => k3s.Admin.AppsV1.DeleteNamespacedDeploymentAsync(name, Ns, propagationPolicy: "Background"));
        }
    }

    [DockerFact]
    public async Task Detail_of_a_missing_pod_is_null()
    {
        await k3s.EnsureNamespaceAsync(Ns);

        var detail = await k3s.Operations.GetPodDetailAsync(k3s.AdminSecret, Ns, "nope");

        detail.Should().BeNull();
    }

    [DockerFact]
    public async Task Delete_pod_removes_it()
    {
        const string name = "delete-pod";
        await SeedEnvPodAsync(name);
        try
        {
            await k3s.Operations.DeletePodAsync(k3s.AdminSecret, Ns, name, 0);

            await K3sFixture.WaitAsync(async () =>
            {
                try
                {
                    await k3s.Admin.CoreV1.ReadNamespacedPodAsync(name, Ns);
                    return false;
                }
                catch (HttpOperationException ex) when ((int)ex.Response.StatusCode == 404)
                {
                    return true;
                }
            }, TimeSpan.FromMinutes(1));
        }
        finally
        {
            await CleanupAsync(name);
        }
    }
}
