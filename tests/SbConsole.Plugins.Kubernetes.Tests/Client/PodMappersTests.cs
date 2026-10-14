using FluentAssertions;
using k8s.Models;
using SbConsole.Plugins.Kubernetes.Client;

namespace SbConsole.Plugins.Kubernetes.Tests.Client;

public class PodMappersTests
{
    private static readonly DateTime Started = new(2026, 10, 14, 11, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Finished = new(2026, 10, 14, 11, 0, 42, DateTimeKind.Utc);

    private static V1Pod Pod(
        IList<V1Container>? containers = null, IList<V1Container>? initContainers = null,
        IList<V1ContainerStatus>? statuses = null, IList<V1ContainerStatus>? initStatuses = null,
        IList<V1Volume>? volumes = null, IList<V1OwnerReference>? owners = null, string? qos = "Burstable") =>
        new()
        {
            Metadata = new V1ObjectMeta { Name = "api-7d9f-abcde", NamespaceProperty = "checkout", OwnerReferences = owners },
            Spec = new V1PodSpec
            {
                NodeName = "node-1", ServiceAccountName = "api-sa",
                Containers = containers ?? [new V1Container { Name = "app", Image = "api:1" }],
                InitContainers = initContainers, Volumes = volumes,
            },
            Status = new V1PodStatus
            {
                Phase = "Running", PodIP = "10.42.0.7", HostIP = "10.0.0.4", QosClass = qos,
                ContainerStatuses = statuses, InitContainerStatuses = initStatuses,
            },
        };

    private static V1ConfigMap Map(string name, Dictionary<string, string> data) =>
        new() { Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = "checkout" }, Data = data };

    // ---- Env ----

    private static V1Container EnvContainer(IList<V1EnvVar>? env = null, IList<V1EnvFromSource>? envFrom = null) => new()
    {
        Name = "app", Image = "api:1", Env = env, EnvFrom = envFrom,
        Resources = new V1ResourceRequirements
        {
            Limits = new Dictionary<string, ResourceQuantity> { ["memory"] = new("512Mi"), ["cpu"] = new("1") },
            Requests = new Dictionary<string, ResourceQuantity> { ["cpu"] = new("250m") },
        },
    };

    private static IReadOnlyList<K8sEnvVar> Env(V1Container container, params V1ConfigMap[] maps) =>
        K8sMappers.Env(Pod(containers: [container]), maps.ToDictionary(m => m.Metadata.Name));

    [Fact]
    public void Literal_value_is_shown_as_is()
    {
        var env = Env(EnvContainer([new V1EnvVar { Name = "MODE", Value = "prod" }]));

        env.Should().Equal(new K8sEnvVar("app", "MODE", "prod", K8sEnvSource.Literal, null));
    }

    [Fact]
    public void Literal_without_a_value_is_an_empty_string()
    {
        var env = Env(EnvContainer([new V1EnvVar { Name = "EMPTY" }]));

        env.Should().Equal(new K8sEnvVar("app", "EMPTY", "", K8sEnvSource.Literal, null));
    }

    [Fact]
    public void ConfigMap_key_ref_reads_the_value_from_the_map()
    {
        var env = Env(
            EnvContainer([new V1EnvVar { Name = "DB_HOST", ValueFrom = new V1EnvVarSource { ConfigMapKeyRef = new V1ConfigMapKeySelector { Name = "api-config", Key = "db.host" } } }]),
            Map("api-config", new() { ["db.host"] = "pg.internal" }));

        env.Should().Equal(new K8sEnvVar("app", "DB_HOST", "pg.internal", K8sEnvSource.ConfigMap, "api-config"));
    }

    [Fact]
    public void ConfigMap_key_ref_with_a_missing_key_is_unresolved()
    {
        var env = Env(
            EnvContainer([new V1EnvVar { Name = "DB_HOST", ValueFrom = new V1EnvVarSource { ConfigMapKeyRef = new V1ConfigMapKeySelector { Name = "api-config", Key = "nope" } } }]),
            Map("api-config", new() { ["db.host"] = "pg.internal" }));

        env.Should().Equal(new K8sEnvVar("app", "DB_HOST", null, K8sEnvSource.Unresolved, "api-config"));
    }

    [Fact]
    public void ConfigMap_key_ref_with_a_missing_map_is_unresolved()
    {
        var env = Env(EnvContainer([new V1EnvVar { Name = "DB_HOST", ValueFrom = new V1EnvVarSource { ConfigMapKeyRef = new V1ConfigMapKeySelector { Name = "gone", Key = "db.host" } } }]));

        env.Should().Equal(new K8sEnvVar("app", "DB_HOST", null, K8sEnvSource.Unresolved, "gone"));
    }

    [Fact]
    public void Secret_key_ref_has_no_value_and_names_the_secret()
    {
        var env = Env(EnvContainer([new V1EnvVar { Name = "DB_PASSWORD", ValueFrom = new V1EnvVarSource { SecretKeyRef = new V1SecretKeySelector { Name = "db-creds", Key = "password" } } }]));

        env.Should().Equal(new K8sEnvVar("app", "DB_PASSWORD", null, K8sEnvSource.Secret, "db-creds"));
    }

    [Theory]
    [InlineData("metadata.name", "api-7d9f-abcde")]
    [InlineData("metadata.namespace", "checkout")]
    [InlineData("spec.nodeName", "node-1")]
    [InlineData("status.podIP", "10.42.0.7")]
    [InlineData("spec.serviceAccountName", "api-sa")]
    [InlineData("metadata.labels['app']", null)]
    public void Field_ref_resolves_known_paths_from_the_pod(string path, string? expected)
    {
        var env = Env(EnvContainer([new V1EnvVar { Name = "X", ValueFrom = new V1EnvVarSource { FieldRef = new V1ObjectFieldSelector { FieldPath = path } } }]));

        env.Should().Equal(new K8sEnvVar("app", "X", expected, K8sEnvSource.FieldRef, path));
    }

    [Theory]
    [InlineData("limits.memory", "512Mi")]
    [InlineData("requests.cpu", "250m")]
    [InlineData("requests.memory", null)]
    public void Resource_field_ref_shows_the_container_quantity(string resource, string? expected)
    {
        var env = Env(EnvContainer([new V1EnvVar { Name = "X", ValueFrom = new V1EnvVarSource { ResourceFieldRef = new V1ResourceFieldSelector { Resource = resource } } }]));

        env.Should().Equal(new K8sEnvVar("app", "X", expected, K8sEnvSource.ResourceFieldRef, resource));
    }

    [Fact]
    public void EnvFrom_config_map_gives_one_row_per_key_with_the_prefix()
    {
        var env = Env(
            EnvContainer(envFrom: [new V1EnvFromSource { Prefix = "APP_", ConfigMapRef = new V1ConfigMapEnvSource { Name = "api-config" } }]),
            Map("api-config", new() { ["b"] = "2", ["a"] = "1" }));

        env.Should().Equal(
            new K8sEnvVar("app", "APP_a", "1", K8sEnvSource.ConfigMap, "api-config"),
            new K8sEnvVar("app", "APP_b", "2", K8sEnvSource.ConfigMap, "api-config"));
    }

    [Fact]
    public void EnvFrom_a_missing_config_map_is_one_unresolved_row()
    {
        var env = Env(EnvContainer(envFrom: [new V1EnvFromSource { ConfigMapRef = new V1ConfigMapEnvSource { Name = "gone" } }]));

        env.Should().Equal(new K8sEnvVar("app", "*", null, K8sEnvSource.Unresolved, "gone"));
    }

    [Fact]
    public void EnvFrom_secret_is_one_prefix_row_without_a_value()
    {
        var env = Env(EnvContainer(envFrom:
        [
            new V1EnvFromSource { Prefix = "DB_", SecretRef = new V1SecretEnvSource { Name = "db-creds" } },
            new V1EnvFromSource { SecretRef = new V1SecretEnvSource { Name = "tokens" } },
        ]));

        env.Should().Equal(
            new K8sEnvVar("app", "DB_*", null, K8sEnvSource.Secret, "db-creds"),
            new K8sEnvVar("app", "*", null, K8sEnvSource.Secret, "tokens"));
    }

    [Fact]
    public void Env_lists_init_containers_first_and_envFrom_before_env()
    {
        var pod = Pod(
            containers: [EnvContainer([new V1EnvVar { Name = "MODE", Value = "prod" }], [new V1EnvFromSource { SecretRef = new V1SecretEnvSource { Name = "s" } }])],
            initContainers: [new V1Container { Name = "migrate", Image = "m:1", Env = [new V1EnvVar { Name = "STEP", Value = "1" }] }]);

        var env = K8sMappers.Env(pod, new Dictionary<string, V1ConfigMap>());

        env.Select(e => $"{e.Container}/{e.Name}").Should().Equal("migrate/STEP", "app/*", "app/MODE");
    }

    [Fact]
    public void Referenced_config_maps_are_the_distinct_env_and_envFrom_names()
    {
        var pod = Pod(containers:
        [
            EnvContainer(
                [
                    new V1EnvVar { Name = "A", ValueFrom = new V1EnvVarSource { ConfigMapKeyRef = new V1ConfigMapKeySelector { Name = "one", Key = "a" } } },
                    new V1EnvVar { Name = "B", ValueFrom = new V1EnvVarSource { ConfigMapKeyRef = new V1ConfigMapKeySelector { Name = "one", Key = "b" } } },
                    new V1EnvVar { Name = "S", ValueFrom = new V1EnvVarSource { SecretKeyRef = new V1SecretKeySelector { Name = "secret", Key = "s" } } },
                ],
                [new V1EnvFromSource { ConfigMapRef = new V1ConfigMapEnvSource { Name = "two" } }, new V1EnvFromSource { SecretRef = new V1SecretEnvSource { Name = "secret2" } }]),
        ]);

        K8sMappers.ReferencedConfigMaps(pod).Should().Equal("one", "two");
    }

    // ---- Containers ----

    [Fact]
    public void Containers_list_init_containers_first()
    {
        var pod = Pod(
            containers: [new V1Container { Name = "app", Image = "api:1" }],
            initContainers: [new V1Container { Name = "migrate", Image = "m:1" }],
            statuses: [new V1ContainerStatus { Name = "app", Image = "api:1", ImageID = "", Ready = true, RestartCount = 0, State = new V1ContainerState { Running = new V1ContainerStateRunning { StartedAt = Started } } }],
            initStatuses: [new V1ContainerStatus { Name = "migrate", Image = "m:1", ImageID = "", Ready = false, RestartCount = 0, State = new V1ContainerState { Terminated = new V1ContainerStateTerminated { ExitCode = 0, Reason = "Completed", StartedAt = Started, FinishedAt = Finished } } }]);

        var containers = K8sMappers.Containers(pod);

        containers.Select(c => (c.Name, c.IsInit, c.State)).Should().Equal(("migrate", true, "Terminated"), ("app", false, "Running"));
        containers[0].StateReason.Should().Be("Completed");
        containers[0].CurrentTermination.Should().Be(new K8sTermination(0, "Completed", new DateTimeOffset(Started), new DateTimeOffset(Finished)));
        containers[1].Ready.Should().BeTrue();
        containers[1].CurrentTermination.Should().BeNull();
    }

    [Fact]
    public void Crash_looping_container_shows_the_OOM_last_termination()
    {
        var pod = Pod(
            containers: [new V1Container
            {
                Name = "gateway", Image = "pg:2.14.1",
                Resources = new V1ResourceRequirements
                {
                    Requests = new Dictionary<string, ResourceQuantity> { ["cpu"] = new("500m"), ["memory"] = new("512Mi") },
                    Limits = new Dictionary<string, ResourceQuantity> { ["memory"] = new("512Mi") },
                },
            }],
            statuses: [new V1ContainerStatus
            {
                Name = "gateway", Image = "pg:2.14.1", ImageID = "", Ready = false, RestartCount = 14,
                State = new V1ContainerState { Waiting = new V1ContainerStateWaiting { Reason = "CrashLoopBackOff" } },
                LastState = new V1ContainerState { Terminated = new V1ContainerStateTerminated { ExitCode = 137, Reason = "OOMKilled", StartedAt = Started, FinishedAt = Finished } },
            }]);

        var c = K8sMappers.Containers(pod).Single();

        c.State.Should().Be("Waiting");
        c.StateReason.Should().Be("CrashLoopBackOff");
        c.Restarts.Should().Be(14);
        c.Ready.Should().BeFalse();
        c.CurrentTermination.Should().BeNull();
        c.LastTermination.Should().Be(new K8sTermination(137, "OOMKilled", new DateTimeOffset(Started), new DateTimeOffset(Finished)));
        c.Resources.Should().Be(new K8sResources(500, 512L * 1024 * 1024, null, 512L * 1024 * 1024));
    }

    [Fact]
    public void Ports_use_the_name_else_the_lowercased_protocol()
    {
        var pod = Pod(containers: [new V1Container
        {
            Name = "app", Image = "api:1",
            Ports = [new V1ContainerPort { ContainerPort = 8080, Name = "http" }, new V1ContainerPort { ContainerPort = 9090, Protocol = "UDP" }, new V1ContainerPort { ContainerPort = 7000 }],
        }]);

        K8sMappers.Containers(pod).Single().Ports.Should().Equal("8080/http", "9090/udp", "7000/tcp");
    }

    [Fact]
    public void Container_without_a_status_is_waiting()
    {
        var c = K8sMappers.Containers(Pod()).Single();

        c.State.Should().Be("Waiting");
        c.StateReason.Should().BeNull();
        c.Restarts.Should().Be(0);
        c.Image.Should().Be("api:1");
    }

    // ---- Owner chain ----

    private static V1OwnerReference Ref(string kind, string name) =>
        new() { ApiVersion = "apps/v1", Kind = kind, Name = name, Uid = Guid.NewGuid().ToString(), Controller = true };

    [Fact]
    public void Owner_chain_is_replica_set_with_revision_then_deployment()
    {
        var pod = Pod(owners: [Ref("ReplicaSet", "api-7d9f")]);
        var rs = new V1ObjectMeta
        {
            Name = "api-7d9f",
            Annotations = new Dictionary<string, string> { ["deployment.kubernetes.io/revision"] = "23" },
            OwnerReferences = [Ref("Deployment", "api")],
        };

        K8sMappers.OwnerChain(pod, rs).Should().Equal(new K8sOwnerLink("ReplicaSet", "api-7d9f", 23), new K8sOwnerLink("Deployment", "api", null));
    }

    [Fact]
    public void Owner_chain_stops_at_the_pod_owner_when_it_could_not_be_read()
    {
        var pod = Pod(owners: [Ref("ReplicaSet", "api-7d9f")]);

        K8sMappers.OwnerChain(pod, null).Should().Equal(new K8sOwnerLink("ReplicaSet", "api-7d9f", null));
    }

    [Fact]
    public void Owner_chain_of_a_cron_job_pod_is_job_then_cron_job()
    {
        var pod = Pod(owners: [Ref("Job", "report-28812")]);
        var job = new V1ObjectMeta { Name = "report-28812", OwnerReferences = [Ref("CronJob", "report")] };

        K8sMappers.OwnerChain(pod, job).Should().Equal(new K8sOwnerLink("Job", "report-28812", null), new K8sOwnerLink("CronJob", "report", null));
    }

    [Fact]
    public void Owner_chain_of_a_bare_pod_is_empty()
    {
        K8sMappers.OwnerChain(Pod(), null).Should().BeEmpty();
    }

    // ---- Volumes and QoS ----

    [Fact]
    public void Secret_mounts_count_secret_volumes_and_projected_secret_sources()
    {
        var pod = Pod(volumes:
        [
            new V1Volume { Name = "tls", Secret = new V1SecretVolumeSource { SecretName = "tls" } },
            new V1Volume { Name = "cfg", ConfigMap = new V1ConfigMapVolumeSource { Name = "cfg" } },
            new V1Volume { Name = "tmp", EmptyDir = new V1EmptyDirVolumeSource() },
            new V1Volume
            {
                Name = "proj",
                Projected = new V1ProjectedVolumeSource
                {
                    Sources =
                    [
                        new V1VolumeProjection { Secret = new V1SecretProjection { Name = "a" } },
                        new V1VolumeProjection { Secret = new V1SecretProjection { Name = "b" } },
                        new V1VolumeProjection { ConfigMap = new V1ConfigMapProjection { Name = "c" } },
                    ],
                },
            },
        ]);

        K8sMappers.VolumeCount(pod).Should().Be(4);
        K8sMappers.SecretMountCount(pod).Should().Be(3);
    }

    [Fact]
    public void Qos_class_falls_back_to_best_effort()
    {
        K8sMappers.QosClass(Pod(qos: "Guaranteed")).Should().Be("Guaranteed");
        K8sMappers.QosClass(Pod(qos: null)).Should().Be("BestEffort");
    }

    // ---- Node health ----

    [Fact]
    public void Node_health_has_ready_pressures_and_memory_request_fraction()
    {
        var node = new K8sNodeInfo("node-1", null, null, null, "True", null, ["MemoryPressure"], ["agent"], [], "v1.30", false,
            4000, 1000, 0, 250, 3, DateTimeOffset.MinValue, null, new Dictionary<string, string>());

        K8sMappers.NodeHealth(node, podsListed: true).Should().BeEquivalentTo(new K8sNodeHealth("node-1", "True", ["MemoryPressure"], 0.25));
        K8sMappers.NodeHealth(node, podsListed: false).MemoryRequestFraction.Should().BeNull();
        K8sMappers.NodeHealth(node with { MemAllocatableBytes = 0 }, podsListed: true).MemoryRequestFraction.Should().BeNull();
    }
}
