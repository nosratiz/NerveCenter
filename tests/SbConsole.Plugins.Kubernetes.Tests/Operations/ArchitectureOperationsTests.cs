using FluentAssertions;
using k8s;
using k8s.Autorest;
using k8s.Models;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Plugins.Kubernetes.Tests.Infrastructure;

namespace SbConsole.Plugins.Kubernetes.Tests.Operations;

[Collection("k3s")]
public class ArchitectureOperationsTests(K3sFixture k3s)
{
    private const string App = "sbc-arch-app";
    private const string Data = "sbc-arch-data";

    private static V1Deployment Deployment(string ns, string name, List<V1EnvVar>? env = null, List<V1EnvFromSource>? envFrom = null)
    {
        var labels = new Dictionary<string, string> { ["app"] = name };
        return new V1Deployment
        {
            Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = ns, Labels = new Dictionary<string, string>(labels) { ["team"] = "core" } },
            Spec = new V1DeploymentSpec
            {
                Replicas = 0,
                Selector = new V1LabelSelector { MatchLabels = labels },
                Template = new V1PodTemplateSpec
                {
                    Metadata = new V1ObjectMeta { Labels = labels },
                    Spec = new V1PodSpec { Containers = [new V1Container { Name = "main", Image = "nginx:1.27-alpine", Env = env, EnvFrom = envFrom }] },
                },
            },
        };
    }

    private async Task SeedAsync()
    {
        await k3s.EnsureNamespaceAsync(App);
        await k3s.EnsureNamespaceAsync(Data);
        await Ignore409(() => k3s.Admin.CoreV1.CreateNamespacedConfigMapAsync(new V1ConfigMap
        {
            Metadata = new V1ObjectMeta { Name = "api-config", NamespaceProperty = App },
            Data = new Dictionary<string, string> { ["DB_HOST"] = "db.sbc-arch-data:5432", ["MODE"] = "live" },
        }, App));
        await k3s.ApplyAsync(Deployment(App, "api",
            [
                new V1EnvVar { Name = "LITERAL", Value = "http://other:8080" },
                new V1EnvVar { Name = "FROM_CM", ValueFrom = new V1EnvVarSource { ConfigMapKeyRef = new V1ConfigMapKeySelector { Name = "api-config", Key = "DB_HOST" } } },
                new V1EnvVar { Name = "FROM_SECRET", ValueFrom = new V1EnvVarSource { SecretKeyRef = new V1SecretKeySelector { Name = "nope", Key = "k" } } },
            ],
            [new V1EnvFromSource { ConfigMapRef = new V1ConfigMapEnvSource { Name = "api-config" }, Prefix = "CM_" }]));
        await k3s.ApplyAsync(Deployment(Data, "db", [new V1EnvVar { Name = "IGNORED", Value = "http://x:1" }]));
        await Ignore409(() => k3s.Admin.CoreV1.CreateNamespacedServiceAsync(Service("api", App), App));
        await Ignore409(() => k3s.Admin.CoreV1.CreateNamespacedServiceAsync(Service("db", Data, 5432), Data));
        await Ignore409(() => k3s.Admin.NetworkingV1.CreateNamespacedIngressAsync(new V1Ingress
        {
            Metadata = new V1ObjectMeta { Name = "web", NamespaceProperty = App },
            Spec = new V1IngressSpec
            {
                Rules =
                [
                    new V1IngressRule
                    {
                        Host = "shop.example.com",
                        Http = new V1HTTPIngressRuleValue
                        {
                            Paths = [new V1HTTPIngressPath { Path = "/", PathType = "Prefix",
                                Backend = new V1IngressBackend { Service = new V1IngressServiceBackend { Name = "api", Port = new V1ServiceBackendPort { Number = 8080 } } } }],
                        },
                    },
                ],
            },
        }, App));
    }

    private static V1Service Service(string name, string ns, int port = 8080) => new()
    {
        Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = ns },
        Spec = new V1ServiceSpec { Selector = new Dictionary<string, string> { ["app"] = name }, Ports = [new V1ServicePort { Port = port }] },
    };

    private static async Task Ignore409(Func<Task> create)
    {
        try
        {
            await create();
        }
        catch (HttpOperationException ex) when ((int)ex.Response.StatusCode == 409)
        {
            // Seeded by an earlier test in this run.
        }
    }

    [DockerFact]
    public async Task Inputs_cover_dependencies_cluster_wide_and_resolve_env_for_the_selection_only()
    {
        await SeedAsync();

        var inputs = await k3s.Operations.GetArchitectureInputsAsync(k3s.AdminSecret, [App]);

        var api = inputs.Workloads.Single(w => w.Namespace == App && w.Name == "api");
        api.Kind.Should().Be(K8sWorkloadKind.Deployment);
        api.PodLabels.Should().Equal(new Dictionary<string, string> { ["app"] = "api" });
        api.Labels["team"].Should().Be("core");
        api.Images.Should().Equal("nginx:1.27-alpine");
        api.Env.Should().Contain(new K8sArchEnvVar("LITERAL", "http://other:8080"));
        api.Env.Should().Contain(new K8sArchEnvVar("FROM_CM", "db.sbc-arch-data:5432"));
        api.Env.Should().Contain(new K8sArchEnvVar("FROM_SECRET", null));
        api.Env.Should().Contain(new K8sArchEnvVar("CM_MODE", "live"));

        var db = inputs.Workloads.Single(w => w.Namespace == Data && w.Name == "db");
        db.Env.Should().BeEmpty("env is only read for the selected namespaces");
        inputs.Services.Should().Contain(s => s.Namespace == Data && s.Name == "db" && s.Ports.SequenceEqual(new[] { 5432 }));
        inputs.Workloads.Should().Contain(w => w.Namespace == "kube-system", "workloads are listed cluster-wide");

        inputs.Ingresses.Should().ContainSingle().Which.Should().Match<K8sArchIngress>(i =>
            i.Name == "web" && i.Hosts.SequenceEqual(new[] { "shop.example.com" }) && i.Services.SequenceEqual(new[] { "api" }));
    }

    [DockerFact]
    public async Task A_viewer_gets_inputs_too()
    {
        await SeedAsync();
        var viewer = await k3s.ViewerSecretAsync();

        var inputs = await k3s.Operations.GetArchitectureInputsAsync(viewer, [App]);

        inputs.Workloads.Should().Contain(w => w.Name == "api");
        inputs.Services.Should().Contain(s => s.Name == "api");
    }
}
