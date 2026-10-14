using FluentAssertions;
using k8s.Models;
using SbConsole.Plugins.Kubernetes.Client;

namespace SbConsole.Plugins.Kubernetes.Tests.Client;

public class K8sMappersTests
{
    private static readonly DateTime Created = new(2026, 10, 14, 10, 0, 0, DateTimeKind.Utc);

    private static V1Container Container(string name, string? cpu = null, string? mem = null, string? cpuLimit = null, string? memLimit = null)
    {
        var requests = new Dictionary<string, ResourceQuantity>();
        var limits = new Dictionary<string, ResourceQuantity>();
        if (cpu is not null) requests["cpu"] = new ResourceQuantity(cpu);
        if (mem is not null) requests["memory"] = new ResourceQuantity(mem);
        if (cpuLimit is not null) limits["cpu"] = new ResourceQuantity(cpuLimit);
        if (memLimit is not null) limits["memory"] = new ResourceQuantity(memLimit);
        return new V1Container { Name = name, Image = name + ":1", Resources = new V1ResourceRequirements { Requests = requests, Limits = limits } };
    }

    private static V1ContainerStatus Status(string name, int restarts = 0, bool ready = true, V1ContainerState? state = null, V1ContainerState? last = null) =>
        new() { Name = name, Image = name + ":1", ImageID = "", RestartCount = restarts, Ready = ready, State = state ?? Running(), LastState = last };

    private static V1ContainerState Running() => new() { Running = new V1ContainerStateRunning { StartedAt = Created } };

    private static V1ContainerState Waiting(string reason) => new() { Waiting = new V1ContainerStateWaiting { Reason = reason } };

    private static V1ContainerState Terminated(string reason, int exit, DateTime? finished = null) =>
        new() { Terminated = new V1ContainerStateTerminated { Reason = reason, ExitCode = exit, FinishedAt = finished } };

    private static V1Pod Pod(
        string phase = "Running", IList<V1Container>? containers = null, IList<V1ContainerStatus>? statuses = null,
        IList<V1ContainerStatus>? initStatuses = null, IList<V1OwnerReference>? owners = null,
        IDictionary<string, string>? labels = null, IDictionary<string, string>? annotations = null,
        IList<V1PodCondition>? conditions = null, DateTime? deleted = null, IList<V1Volume>? volumes = null) =>
        new()
        {
            Metadata = new V1ObjectMeta
            {
                Name = "api-7d9f-abcde", NamespaceProperty = "checkout", CreationTimestamp = Created,
                OwnerReferences = owners, Labels = labels, Annotations = annotations, DeletionTimestamp = deleted,
            },
            Spec = new V1PodSpec
            {
                NodeName = "node-1",
                Containers = containers ?? [Container("app")],
                InitContainers = [Container("init", cpu: "5")],
                Volumes = volumes,
            },
            Status = new V1PodStatus
            {
                Phase = phase, ContainerStatuses = statuses ?? [Status("app")], InitContainerStatuses = initStatuses,
                Conditions = conditions,
            },
        };

    [Fact]
    public void Running_pod_maps_basic_fields()
    {
        var p = K8sMappers.Pod(Pod());

        p.Namespace.Should().Be("checkout");
        p.Name.Should().Be("api-7d9f-abcde");
        p.Phase.Should().Be("Running");
        p.Status.Should().Be("Running");
        p.ReadyContainers.Should().Be(1);
        p.TotalContainers.Should().Be(1);
        p.NodeName.Should().Be("node-1");
        p.CreatedAt.Should().Be(new DateTimeOffset(Created));
        p.IsDaemonSetPod.Should().BeFalse();
        p.IsMirrorPod.Should().BeFalse();
        p.HasEmptyDir.Should().BeFalse();
        p.Labels.Should().BeEmpty();
    }

    [Fact]
    public void Crash_looping_container_sets_status_and_restarts_and_last_termination()
    {
        var finished = Created.AddMinutes(5);
        var pod = Pod(
            containers: [Container("app"), Container("sidecar")],
            statuses:
            [
                Status("app", restarts: 7, ready: false, state: Waiting("CrashLoopBackOff"), last: Terminated("OOMKilled", 137, finished)),
                Status("sidecar", restarts: 2, last: Terminated("Error", 1)),
            ]);

        var p = K8sMappers.Pod(pod);

        p.Status.Should().Be("CrashLoopBackOff");
        p.Restarts.Should().Be(9);
        p.ReadyContainers.Should().Be(1);
        p.TotalContainers.Should().Be(2);
        p.LastTerminationReason.Should().Be("OOMKilled");
        p.LastExitCode.Should().Be(137);
        p.LastTerminatedAt.Should().Be(new DateTimeOffset(finished));
    }

    [Fact]
    public void Init_container_waiting_reason_wins_over_app_containers()
    {
        var pod = Pod(
            phase: "Pending",
            statuses: [Status("app", ready: false, state: Waiting("PodInitializing"))],
            initStatuses: [Status("init", restarts: 4, ready: false, state: Waiting("ImagePullBackOff"))]);

        var p = K8sMappers.Pod(pod);

        p.Status.Should().Be("ImagePullBackOff");
        p.Restarts.Should().Be(0, "only app containers' restarts count");
    }

    [Fact]
    public void Terminated_oom_container_in_running_pod_shows_the_reason()
    {
        var pod = Pod(statuses: [Status("app", ready: false, state: Terminated("OOMKilled", 137))]);

        K8sMappers.Pod(pod).Status.Should().Be("OOMKilled");
    }

    [Fact]
    public void Succeeded_pod_is_completed_and_deleting_pod_is_terminating()
    {
        K8sMappers.Pod(Pod(phase: "Succeeded", statuses: [Status("app", ready: false, state: Terminated("Completed", 0))]))
            .Status.Should().Be("Completed");
        K8sMappers.Pod(Pod(deleted: Created, statuses: [Status("app", state: Waiting("CrashLoopBackOff"))]))
            .Status.Should().Be("Terminating");
    }

    [Fact]
    public void Unschedulable_condition_message_is_captured()
    {
        var pod = Pod(phase: "Pending", statuses: [],
            conditions:
            [
                new V1PodCondition
                {
                    Type = "PodScheduled", Status = "False", Reason = "Unschedulable",
                    Message = "0/1 nodes are available: 1 Insufficient memory.",
                },
            ]);

        var p = K8sMappers.Pod(pod);

        p.Status.Should().Be("Pending");
        p.UnschedulableMessage.Should().Be("0/1 nodes are available: 1 Insufficient memory.");
        p.ReadyContainers.Should().Be(0);
    }

    [Fact]
    public void Replica_set_owner_with_matching_template_hash_becomes_the_deployment()
    {
        var pod = Pod(
            owners: [new V1OwnerReference { Kind = "ReplicaSet", Name = "api-7d9f", Controller = true, ApiVersion = "apps/v1", Uid = "u" }],
            labels: new Dictionary<string, string> { ["pod-template-hash"] = "7d9f", ["app"] = "api" });

        var p = K8sMappers.Pod(pod);

        p.Owner.Should().Be(new K8sRef("Deployment", "checkout", "api"));
        p.ReplicaSet.Should().Be("api-7d9f");
        p.Labels.Should().ContainKey("app");
    }

    [Fact]
    public void Replica_set_owner_without_matching_hash_stays_a_replica_set()
    {
        var pod = Pod(owners: [new V1OwnerReference { Kind = "ReplicaSet", Name = "bare-rs", Controller = true, ApiVersion = "apps/v1", Uid = "u" }]);

        var p = K8sMappers.Pod(pod);

        p.Owner.Should().Be(new K8sRef("ReplicaSet", "checkout", "bare-rs"));
        p.ReplicaSet.Should().Be("bare-rs");
    }

    [Fact]
    public void Daemon_set_and_mirror_pods_are_flagged()
    {
        var ds = Pod(owners: [new V1OwnerReference { Kind = "DaemonSet", Name = "agent", Controller = true, ApiVersion = "apps/v1", Uid = "u" }]);
        var mirror = Pod(annotations: new Dictionary<string, string> { ["kubernetes.io/config.mirror"] = "abc" });

        K8sMappers.Pod(ds).IsDaemonSetPod.Should().BeTrue();
        K8sMappers.Pod(ds).Owner.Should().Be(new K8sRef("DaemonSet", "checkout", "agent"));
        K8sMappers.Pod(mirror).IsMirrorPod.Should().BeTrue();
    }

    [Fact]
    public void Requests_sum_app_containers_and_ignore_init_containers()
    {
        var pod = Pod(containers: [Container("a", cpu: "250m", mem: "128Mi"), Container("b", cpu: "0.5", mem: "1Gi"), Container("c")]);

        var p = K8sMappers.Pod(pod);

        p.CpuRequestMilli.Should().Be(750);
        p.MemRequestBytes.Should().Be(128L * 1024 * 1024 + 1024L * 1024 * 1024);
    }

    [Fact]
    public void Limits_sum_app_containers()
    {
        var pod = Pod(containers: [Container("a", cpuLimit: "1", memLimit: "256Mi"), Container("b", cpuLimit: "500m")]);

        K8sMappers.PodLimits(pod).Should().Be((1500L, 256L * 1024 * 1024));
    }

    [Fact]
    public void Empty_dir_volume_is_detected()
    {
        var pod = Pod(volumes: [new V1Volume { Name = "scratch", EmptyDir = new V1EmptyDirVolumeSource() }]);

        K8sMappers.Pod(pod).HasEmptyDir.Should().BeTrue();
    }

    // ---- Nodes ----

    private static V1Node Node(
        string ready = "True", IDictionary<string, string>? labels = null, IList<V1Taint>? taints = null,
        IList<V1NodeCondition>? extra = null, string? providerId = null, bool unschedulable = false)
    {
        var conditions = new List<V1NodeCondition>
        {
            new() { Type = "Ready", Status = ready, LastTransitionTime = Created },
            new() { Type = "MemoryPressure", Status = "False" },
        };
        conditions.AddRange(extra ?? []);
        return new V1Node
        {
            Metadata = new V1ObjectMeta { Name = "node-1", CreationTimestamp = Created, Labels = labels ?? new Dictionary<string, string>() },
            Spec = new V1NodeSpec { Taints = taints, ProviderID = providerId, Unschedulable = unschedulable },
            Status = new V1NodeStatus
            {
                Conditions = conditions,
                Allocatable = new Dictionary<string, ResourceQuantity> { ["cpu"] = new("3860m"), ["memory"] = new("16Gi") },
                Addresses = [new V1NodeAddress { Type = "InternalIP", Address = "10.0.0.4" }],
                NodeInfo = new V1NodeSystemInfo { KubeletVersion = "v1.30.4" },
            },
        };
    }

    private static K8sPodSummary PodSummary(string phase, long cpu, long mem) =>
        K8sMappers.Pod(Pod(phase: phase)) with { CpuRequestMilli = cpu, MemRequestBytes = mem };

    [Fact]
    public void Node_maps_ready_allocatable_and_requested_from_non_terminal_pods()
    {
        var node = Node(labels: new Dictionary<string, string>
        {
            ["node.kubernetes.io/instance-type"] = "Standard_D4s_v5", ["agentpool"] = "system",
        }, providerId: "azure:///subscriptions/x/vm-0", unschedulable: true);
        var pods = new[] { PodSummary("Running", 500, 100), PodSummary("Pending", 250, 50), PodSummary("Succeeded", 1000, 1000) };

        var n = K8sMappers.Node(node, pods);

        n.Name.Should().Be("node-1");
        n.ReadyStatus.Should().Be("True");
        n.ReadySince.Should().Be(new DateTimeOffset(Created));
        n.InternalIp.Should().Be("10.0.0.4");
        n.InstanceType.Should().Be("Standard_D4s_v5");
        n.Pool.Should().Be("system");
        n.KubeletVersion.Should().Be("v1.30.4");
        n.Unschedulable.Should().BeTrue();
        n.CpuAllocatableMilli.Should().Be(3860);
        n.MemAllocatableBytes.Should().Be(16L * 1024 * 1024 * 1024);
        n.CpuRequestedMilli.Should().Be(750);
        n.MemRequestedBytes.Should().Be(150);
        n.PodCount.Should().Be(2);
        n.ProviderId.Should().Be("azure:///subscriptions/x/vm-0");
        n.Pressures.Should().BeEmpty();
    }

    [Fact]
    public void Node_pressures_roles_and_taints()
    {
        var node = Node(
            ready: "False",
            labels: new Dictionary<string, string>
            {
                ["node-role.kubernetes.io/control-plane"] = "", ["node-role.kubernetes.io/master"] = "true",
            },
            taints:
            [
                new V1Taint { Key = "dedicated", Value = "gpu", Effect = "NoSchedule" },
                new V1Taint { Key = "CriticalAddonsOnly", Effect = "NoExecute" },
                new V1Taint { Key = "node.kubernetes.io/unreachable", Effect = "NoExecute" },
            ],
            extra:
            [
                new V1NodeCondition { Type = "DiskPressure", Status = "True" },
                new V1NodeCondition { Type = "PIDPressure", Status = "True" },
            ]);

        var n = K8sMappers.Node(node, []);

        n.ReadyStatus.Should().Be("False");
        n.Pressures.Should().Equal("DiskPressure", "PIDPressure");
        n.Roles.Should().Equal("control-plane", "master");
        n.Taints.Should().Equal("dedicated=gpu:NoSchedule", "CriticalAddonsOnly:NoExecute");
    }

    [Fact]
    public void Node_without_role_labels_is_an_agent_and_missing_ready_is_unknown()
    {
        var node = Node();
        node.Status.Conditions = [];

        var n = K8sMappers.Node(node, []);

        n.Roles.Should().Equal("agent");
        n.ReadyStatus.Should().Be("Unknown");
        n.ReadySince.Should().BeNull();
    }

    [Theory]
    [InlineData("agentpool", "system")]
    [InlineData("kubernetes.azure.com/agentpool", "user1")]
    [InlineData("eks.amazonaws.com/nodegroup", "ng-a")]
    [InlineData("cloud.google.com/gke-nodepool", "default-pool")]
    public void Pool_comes_from_well_known_labels(string label, string value) =>
        K8sMappers.Pool(new Dictionary<string, string> { [label] = value }).Should().Be(value);

    [Fact]
    public void Pool_is_null_without_labels() => K8sMappers.Pool(new Dictionary<string, string>()).Should().BeNull();

    [Theory]
    [InlineData("azure:///subscriptions/x", null, "AKS")]
    [InlineData("aws:///eu-west-1a/i-1", null, "EKS")]
    [InlineData("gce://proj/zone/vm", null, "GKE")]
    [InlineData("k3s://node-1", null, "k3s")]
    [InlineData("kind://docker/kind/kind-control-plane", null, "kind")]
    [InlineData(null, "k3s", "k3s")]
    [InlineData(null, "m5.large", "Kubernetes")]
    [InlineData(null, null, "Kubernetes")]
    public void Provider_is_detected(string? providerId, string? instanceType, string expected)
    {
        var labels = new Dictionary<string, string>();
        if (instanceType is not null) labels["node.kubernetes.io/instance-type"] = instanceType;
        var node = K8sMappers.Node(Node(labels: labels, providerId: providerId), []);

        K8sMappers.Provider([node]).Should().Be(expected);
    }

    [Fact]
    public void Provider_with_no_nodes_is_kubernetes() => K8sMappers.Provider([]).Should().Be("Kubernetes");

    // ---- Events ----

    [Fact]
    public void Event_maps_regarding_count_and_last_seen()
    {
        var last = Created.AddMinutes(3);
        var e = new Eventsv1Event
        {
            Metadata = new V1ObjectMeta { Name = "e1", NamespaceProperty = "checkout", CreationTimestamp = Created },
            Type = "Warning", Reason = "BackOff", Note = "Back-off restarting failed container",
            Regarding = new V1ObjectReference { Kind = "Pod", NamespaceProperty = "checkout", Name = "api-1" },
            DeprecatedCount = 12, DeprecatedLastTimestamp = last, ReportingController = "kubelet",
        };

        var ev = K8sMappers.Event(e);

        ev.Should().Be(new K8sEvent("checkout", "Warning", "BackOff", "Back-off restarting failed container",
            new K8sRef("Pod", "checkout", "api-1"), 12, new DateTimeOffset(last), "kubelet"));
    }

    [Fact]
    public void Event_series_and_event_time_are_used_when_present()
    {
        var first = Created.AddMinutes(1);
        var latest = Created.AddMinutes(9);
        var e = new Eventsv1Event
        {
            Metadata = new V1ObjectMeta { Name = "e1", NamespaceProperty = "ns", CreationTimestamp = Created },
            Type = "Warning", Reason = "FailedScheduling", Note = "0/1 nodes",
            Regarding = new V1ObjectReference { Kind = "Pod", NamespaceProperty = "ns", Name = "p" },
            EventTime = first, Series = new Eventsv1EventSeries { Count = 4, LastObservedTime = latest },
            ReportingController = "default-scheduler",
        };

        var ev = K8sMappers.Event(e);

        ev.Count.Should().Be(4);
        ev.LastSeen.Should().Be(new DateTimeOffset(latest));
    }

    [Fact]
    public void Event_without_times_falls_back_to_creation_and_count_one()
    {
        var e = new Eventsv1Event
        {
            Metadata = new V1ObjectMeta { Name = "e1", CreationTimestamp = Created },
            Type = "Warning", Reason = "R", Regarding = new V1ObjectReference { Kind = "Node", Name = "node-1" },
        };

        var ev = K8sMappers.Event(e);

        ev.Count.Should().Be(1);
        ev.LastSeen.Should().Be(new DateTimeOffset(Created));
        ev.Message.Should().BeEmpty();
        ev.Namespace.Should().BeNull();
    }

    [Fact]
    public void Core_v1_event_maps_like_an_events_k8s_io_event()
    {
        var last = Created.AddMinutes(3);
        var e = new Corev1Event
        {
            Metadata = new V1ObjectMeta { Name = "e1", NamespaceProperty = "checkout", CreationTimestamp = Created },
            Type = "Warning", Reason = "BackOff", Message = "Back-off restarting failed container",
            InvolvedObject = new V1ObjectReference { Kind = "Pod", NamespaceProperty = "checkout", Name = "api-1" },
            Count = 12, LastTimestamp = last, Source = new V1EventSource { Component = "kubelet" },
        };

        K8sMappers.Event(e).Should().Be(new K8sEvent("checkout", "Warning", "BackOff", "Back-off restarting failed container",
            new K8sRef("Pod", "checkout", "api-1"), 12, new DateTimeOffset(last), "kubelet"));
    }

    // ---- Workloads ----

    private static V1PodTemplateSpec Template(params string[] images) =>
        new() { Spec = new V1PodSpec { Containers = images.Select((i, n) => new V1Container { Name = "c" + n, Image = i }).ToList() } };

    [Fact]
    public void Deployment_maps_replicas_images_and_conditions()
    {
        var d = new V1Deployment
        {
            Metadata = new V1ObjectMeta { Name = "api", NamespaceProperty = "checkout", CreationTimestamp = Created, Labels = new Dictionary<string, string> { ["app"] = "api" } },
            Spec = new V1DeploymentSpec { Replicas = 3, Template = Template("api:2", "envoy:1"), Selector = new V1LabelSelector() },
            Status = new V1DeploymentStatus
            {
                ReadyReplicas = 2, UpdatedReplicas = 3, AvailableReplicas = 2,
                Conditions = [new V1DeploymentCondition { Type = "Available", Status = "False", Reason = "MinimumReplicasUnavailable", LastTransitionTime = Created }],
            },
        };

        var w = K8sMappers.Deployment(d);

        w.Kind.Should().Be(K8sWorkloadKind.Deployment);
        (w.Namespace, w.Name, w.Desired, w.Ready, w.UpToDate, w.Available).Should().Be(("checkout", "api", 3, 2, 3, 2));
        w.Images.Should().Equal("api:2", "envoy:1");
        w.Labels["app"].Should().Be("api");
        w.Conditions.Should().Equal(new K8sCondition("Available", "False", "MinimumReplicasUnavailable", null, new DateTimeOffset(Created)));
        w.IsUnhealthy.Should().BeTrue();
        w.JobStatus.Should().BeNull();
    }

    [Fact]
    public void Stateful_and_daemon_sets_map_their_counts()
    {
        var s = K8sMappers.StatefulSet(new V1StatefulSet
        {
            Metadata = new V1ObjectMeta { Name = "db", NamespaceProperty = "data", CreationTimestamp = Created },
            Spec = new V1StatefulSetSpec { Replicas = 3, Template = Template("pg:16"), Selector = new V1LabelSelector(), ServiceName = "db" },
            Status = new V1StatefulSetStatus { Replicas = 3, ReadyReplicas = 3, UpdatedReplicas = 3, AvailableReplicas = 3 },
        });
        var d = K8sMappers.DaemonSet(new V1DaemonSet
        {
            Metadata = new V1ObjectMeta { Name = "agent", NamespaceProperty = "kube-system", CreationTimestamp = Created },
            Spec = new V1DaemonSetSpec { Template = Template("agent:1"), Selector = new V1LabelSelector() },
            Status = new V1DaemonSetStatus { DesiredNumberScheduled = 4, NumberReady = 3, UpdatedNumberScheduled = 4, NumberAvailable = 3 },
        });

        (s.Kind, s.Desired, s.Ready, s.UpToDate, s.Available).Should().Be((K8sWorkloadKind.StatefulSet, 3, 3, 3, 3));
        (d.Kind, d.Desired, d.Ready, d.UpToDate, d.Available).Should().Be((K8sWorkloadKind.DaemonSet, 4, 3, 4, 3));
        d.IsUnhealthy.Should().BeTrue();
    }

    [Fact]
    public void Failed_job_has_status_reason_and_duration()
    {
        var j = new V1Job
        {
            Metadata = new V1ObjectMeta { Name = "migrate", NamespaceProperty = "checkout", CreationTimestamp = Created },
            Spec = new V1JobSpec { Completions = 1, Template = Template("migrate:1") },
            Status = new V1JobStatus
            {
                Failed = 2, StartTime = Created,
                Conditions = [new V1JobCondition { Type = "Failed", Status = "True", Reason = "BackoffLimitExceeded", Message = "Job has reached the specified backoff limit", LastTransitionTime = Created.AddMinutes(2) }],
            },
        };

        var w = K8sMappers.Job(j, new DateTimeOffset(Created.AddHours(1)));

        w.Kind.Should().Be(K8sWorkloadKind.Job);
        w.JobStatus.Should().Be("Failed");
        w.FailureReason.Should().Be("BackoffLimitExceeded");
        w.Failed.Should().Be(2);
        w.Completions.Should().Be(1);
        w.Duration.Should().Be(TimeSpan.FromMinutes(2));
        w.IsUnhealthy.Should().BeTrue();
    }

    [Fact]
    public void Running_complete_and_suspended_jobs()
    {
        V1Job Job(V1JobStatus status, bool suspend = false) => new()
        {
            Metadata = new V1ObjectMeta { Name = "j", NamespaceProperty = "ns", CreationTimestamp = Created },
            Spec = new V1JobSpec { Template = Template("j:1"), Suspend = suspend },
            Status = status,
        };
        var now = new DateTimeOffset(Created.AddMinutes(10));

        var running = K8sMappers.Job(Job(new V1JobStatus { Active = 1, StartTime = Created }), now);
        var complete = K8sMappers.Job(Job(new V1JobStatus
        {
            Succeeded = 1, StartTime = Created, CompletionTime = Created.AddSeconds(30),
            Conditions = [new V1JobCondition { Type = "Complete", Status = "True" }],
        }), now);
        var suspended = K8sMappers.Job(Job(new V1JobStatus(), suspend: true), now);

        (running.JobStatus, running.Active, running.Duration).Should().Be(("Running", 1, TimeSpan.FromMinutes(10)));
        (complete.JobStatus, complete.Succeeded, complete.Duration).Should().Be(("Complete", 1, TimeSpan.FromSeconds(30)));
        suspended.JobStatus.Should().Be("Suspended");
        suspended.Suspended.Should().BeTrue();
        suspended.Duration.Should().BeNull();
    }

    [Fact]
    public void Cron_job_maps_schedule_and_last_schedule()
    {
        var c = new V1CronJob
        {
            Metadata = new V1ObjectMeta { Name = "nightly", NamespaceProperty = "ops", CreationTimestamp = Created },
            Spec = new V1CronJobSpec
            {
                Schedule = "0 3 * * *", Suspend = true,
                JobTemplate = new V1JobTemplateSpec { Spec = new V1JobSpec { Template = Template("backup:1") } },
            },
            Status = new V1CronJobStatus { LastScheduleTime = Created, Active = [new V1ObjectReference { Name = "x" }] },
        };

        var w = K8sMappers.CronJob(c);

        w.Kind.Should().Be(K8sWorkloadKind.CronJob);
        w.Schedule.Should().Be("0 3 * * *");
        w.Suspended.Should().BeTrue();
        w.LastSchedule.Should().Be(new DateTimeOffset(Created));
        w.Active.Should().Be(1);
        w.Images.Should().Equal("backup:1");
        w.IsUnhealthy.Should().BeFalse();
    }

    [Fact]
    public void Pdb_maps_selector_rule_and_status()
    {
        var pdb = new V1PodDisruptionBudget
        {
            Metadata = new V1ObjectMeta { Name = "kafka", NamespaceProperty = "platform" },
            Spec = new V1PodDisruptionBudgetSpec
            {
                MaxUnavailable = "25%",
                Selector = new V1LabelSelector { MatchLabels = new Dictionary<string, string> { ["app"] = "kafka" } },
            },
            Status = new V1PodDisruptionBudgetStatus { DisruptionsAllowed = 0, CurrentHealthy = 3, DesiredHealthy = 3, ExpectedPods = 3 },
        };

        var mapped = K8sMappers.Pdb(pdb);

        mapped.Should().BeEquivalentTo(new K8sPdb("platform", "kafka", new Dictionary<string, string> { ["app"] = "kafka" }, 0, "maxUnavailable 25%", 3, 3));
        K8sMappers.Pdb(new V1PodDisruptionBudget
        {
            Metadata = new V1ObjectMeta { Name = "ledger", NamespaceProperty = "payments" },
            Spec = new V1PodDisruptionBudgetSpec { MinAvailable = 3, Selector = new V1LabelSelector() },
        }).Rule.Should().Be("minAvailable 3");
    }
}
