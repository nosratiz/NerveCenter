using FluentAssertions;
using k8s.Models;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Plugins.Kubernetes.Config;

namespace SbConsole.Plugins.Kubernetes.Tests.Config;

public class ConfigMapUsageTests
{
    private static K8sConfigMap Map(string name, string ns = "checkout") =>
        new(ns, name, new Dictionary<string, string> { ["k"] = "v" }, new Dictionary<string, long>(), TestData.T0, "1");

    private static ConfigMapRow RowOf(IReadOnlyList<ConfigMapRow> rows, string name) => rows.Single(r => r.Map.Name == name);

    [Fact]
    public void Env_and_volume_refs_from_one_owner_group_into_one_user()
    {
        var rows = ConfigMapUsage.Resolve(TestData.ConfigData());

        var gateway = RowOf(rows, "gateway-config");
        gateway.Unused.Should().BeFalse();
        gateway.UsedBy.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new ConfigMapUser("payment-gateway", ["env", "volume"]), o => o.WithStrictOrdering());
    }

    [Fact]
    public void A_map_nobody_references_is_unused()
    {
        var rows = ConfigMapUsage.Resolve(TestData.ConfigData());

        var reports = RowOf(rows, "reports-config");
        reports.Unused.Should().BeTrue();
        reports.UsedBy.Should().BeEmpty();
    }

    [Fact]
    public void Kube_root_ca_is_all_pods_projected_when_only_the_token_volume_references_it()
    {
        var rows = ConfigMapUsage.Resolve(TestData.ConfigData());

        RowOf(rows, "kube-root-ca.crt").UsedBy.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new ConfigMapUser("all pods", ["projected"]));
    }

    [Fact]
    public void Kube_root_ca_with_no_refs_at_all_is_still_all_pods_projected()
    {
        var rows = ConfigMapUsage.Resolve(new K8sConfigData([Map("kube-root-ca.crt")], []));

        RowOf(rows, "kube-root-ca.crt").UsedBy.Should().ContainSingle().Which.Name.Should().Be("all pods");
    }

    [Fact]
    public void Kube_root_ca_referenced_explicitly_lists_those_users()
    {
        var rows = ConfigMapUsage.Resolve(new K8sConfigData([Map("kube-root-ca.crt")],
        [
            new("checkout", "kube-root-ca.crt", "api", "projected"),
            new("checkout", "kube-root-ca.crt", "ca-sync", "volume"),
        ]));

        RowOf(rows, "kube-root-ca.crt").UsedBy.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new ConfigMapUser("ca-sync", ["volume"]));
    }

    [Fact]
    public void Users_are_ordered_by_name_vias_in_env_volume_projected_order_and_duplicates_collapse()
    {
        var rows = ConfigMapUsage.Resolve(new K8sConfigData([Map("shared")],
        [
            new("checkout", "shared", "worker", "projected"),
            new("checkout", "shared", "worker", "env"),
            new("checkout", "shared", "worker", "env"),
            new("checkout", "shared", "api", "volume"),
        ]));

        RowOf(rows, "shared").UsedBy.Should().BeEquivalentTo(
            [new ConfigMapUser("api", ["volume"]), new ConfigMapUser("worker", ["env", "projected"])], o => o.WithStrictOrdering());
    }

    [Fact]
    public void Refs_only_count_in_their_own_namespace()
    {
        var rows = ConfigMapUsage.Resolve(new K8sConfigData([Map("app", "checkout"), Map("app", "payments")],
        [
            new("payments", "app", "settlement", "env"),
        ]));

        rows.Single(r => r.Map.Namespace == "checkout").Unused.Should().BeTrue();
        rows.Single(r => r.Map.Namespace == "payments").UsedBy.Single().Name.Should().Be("settlement");
    }

    [Fact]
    public void Rows_are_ordered_by_namespace_then_name()
    {
        var rows = ConfigMapUsage.Resolve(new K8sConfigData([Map("b", "payments"), Map("z"), Map("a")], []));

        rows.Select(r => $"{r.Map.Namespace}/{r.Map.Name}").Should().Equal("checkout/a", "checkout/z", "payments/b");
    }

    [Fact]
    public void Pod_config_refs_cover_env_envFrom_volumes_and_projected_sources_and_name_the_top_level_owner()
    {
        var pod = new V1Pod
        {
            Metadata = new V1ObjectMeta
            {
                Name = "payment-gateway-7d9f8c6b5-abcde", NamespaceProperty = "checkout",
                Labels = new Dictionary<string, string> { ["pod-template-hash"] = "7d9f8c6b5" },
                OwnerReferences = [new V1OwnerReference { Kind = "ReplicaSet", Name = "payment-gateway-7d9f8c6b5", Controller = true, ApiVersion = "apps/v1", Uid = "u" }],
            },
            Spec = new V1PodSpec
            {
                InitContainers = [new V1Container { Name = "init", EnvFrom = [new V1EnvFromSource { ConfigMapRef = new V1ConfigMapEnvSource { Name = "init-config" } }] }],
                Containers =
                [
                    new V1Container
                    {
                        Name = "app",
                        Env = [new V1EnvVar { Name = "REDIS_URL", ValueFrom = new V1EnvVarSource { ConfigMapKeyRef = new V1ConfigMapKeySelector { Name = "gateway-config", Key = "REDIS_URL" } } }],
                    },
                ],
                Volumes =
                [
                    new V1Volume { Name = "cfg", ConfigMap = new V1ConfigMapVolumeSource { Name = "gateway-config" } },
                    new V1Volume
                    {
                        Name = "kube-api-access",
                        Projected = new V1ProjectedVolumeSource { Sources = [new V1VolumeProjection { ConfigMap = new V1ConfigMapProjection { Name = "kube-root-ca.crt" } }] },
                    },
                ],
            },
        };

        var refs = K8sMappers.ConfigRefs(pod);

        refs.Should().BeEquivalentTo(
        [
            new K8sConfigRef("checkout", "init-config", "payment-gateway", "env"),
            new K8sConfigRef("checkout", "gateway-config", "payment-gateway", "env"),
            new K8sConfigRef("checkout", "gateway-config", "payment-gateway", "volume"),
            new K8sConfigRef("checkout", "kube-root-ca.crt", "payment-gateway", "projected"),
        ]);
    }

    [Fact]
    public void A_bare_pod_is_its_own_user()
    {
        var pod = new V1Pod
        {
            Metadata = new V1ObjectMeta { Name = "debug", NamespaceProperty = "checkout" },
            Spec = new V1PodSpec
            {
                Containers = [new V1Container { Name = "c", EnvFrom = [new V1EnvFromSource { ConfigMapRef = new V1ConfigMapEnvSource { Name = "cm" } }] }],
            },
        };

        K8sMappers.ConfigRefs(pod).Should().Equal(new K8sConfigRef("checkout", "cm", "debug", "env"));
    }
}
