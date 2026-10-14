using System.Net;
using FluentAssertions;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Plugins.Kubernetes.Tests.Infrastructure;

namespace SbConsole.Plugins.Kubernetes.Tests.Operations;

[Collection("k3s")]
public class EventOperationsTests(K3sFixture k3s)
{
    private const string Ns = "sbc-events";
    private static readonly string[] Scope = [Ns];

    [DockerFact]
    public async Task A_failing_pod_produces_a_warning_event_reported_by_the_kubelet()
    {
        await CreateCrasherAsync();

        IReadOnlyList<K8sEvent> events = [];
        await K3sFixture.WaitAsync(async () =>
        {
            events = await k3s.Operations.ListEventsAsync(k3s.AdminSecret, Scope, TimeSpan.FromHours(1));
            return events.Any(IsCrasherBackOff);
        }, TimeSpan.FromSeconds(90));

        var backOff = events.First(IsCrasherBackOff);
        backOff.Count.Should().BeGreaterThanOrEqualTo(1);
        backOff.Reporter.Should().Be("kubelet");
        backOff.Namespace.Should().Be(Ns);
        backOff.Regarding.Should().Be(new K8sRef("Pod", Ns, "crasher"));
        events.Should().Contain(e => e.Type == "Normal", "the window holds every type; the handler filters");
        events.Should().OnlyContain(e => e.Namespace == Ns);
        events.Select(e => e.LastSeen).Should().BeInDescendingOrder();
    }

    [DockerFact]
    public async Task A_viewer_without_events_k8s_io_still_lists_events_through_core_v1()
    {
        await CreateCrasherAsync();
        var viewer = await k3s.ViewerSecretAsync();

        IReadOnlyList<K8sEvent> events = [];
        await K3sFixture.WaitAsync(async () =>
        {
            events = await k3s.Operations.ListEventsAsync(viewer, Scope, TimeSpan.FromHours(1));
            return events.Any(IsCrasherBackOff);
        }, TimeSpan.FromSeconds(90));

        events.First(IsCrasherBackOff).Reporter.Should().Be("kubelet");
    }

    [DockerFact]
    public async Task K3s_supports_per_object_field_selectors_on_both_event_apis()
    {
        await CreateCrasherAsync();
        await K3sFixture.WaitAsync(async () =>
            (await k3s.Admin.CoreV1.ListNamespacedEventAsync(Ns, fieldSelector: "involvedObject.kind=Pod,involvedObject.name=crasher")).Items.Count > 0,
            TimeSpan.FromSeconds(90));

        var v1 = await k3s.Admin.EventsV1.ListNamespacedEventAsync(Ns, fieldSelector: "regarding.kind=Pod,regarding.name=crasher");
        var core = await k3s.Admin.CoreV1.ListNamespacedEventAsync(Ns, fieldSelector: "involvedObject.kind=Pod,involvedObject.name=crasher");
        var none = await k3s.Admin.EventsV1.ListNamespacedEventAsync(Ns, fieldSelector: "regarding.kind=Pod,regarding.name=no-such-pod");

        v1.Items.Should().NotBeEmpty().And.OnlyContain(e => e.Regarding.Kind == "Pod" && e.Regarding.Name == "crasher");
        core.Items.Should().NotBeEmpty().And.OnlyContain(e => e.InvolvedObject.Kind == "Pod" && e.InvolvedObject.Name == "crasher");
        none.Items.Should().BeEmpty();
    }

    [DockerFact]
    public async Task Pod_detail_events_come_from_a_per_object_query_for_admin_and_viewer()
    {
        await CreateCrasherAsync();
        var viewer = await k3s.ViewerSecretAsync();

        foreach (var secret in new[] { k3s.AdminSecret, viewer })
        {
            K8sPodDetail? detail = null;
            await K3sFixture.WaitAsync(async () =>
            {
                detail = await k3s.Operations.GetPodDetailAsync(secret, Ns, "crasher");
                return detail!.Events.Any(IsCrasherBackOff);
            }, TimeSpan.FromSeconds(90));

            detail!.Events.Should().OnlyContain(e => e.Regarding.Kind == "Pod" && e.Regarding.Name == "crasher");
        }
    }

    [DockerFact]
    public async Task Events_for_a_selected_namespace_are_listed_per_namespace_for_admin_and_viewer()
    {
        await CreateCrasherAsync();
        var viewer = await k3s.ViewerSecretAsync();

        foreach (var secret in new[] { k3s.AdminSecret, viewer })
        {
            IReadOnlyList<K8sEvent> events = [];
            await K3sFixture.WaitAsync(async () =>
            {
                events = await k3s.Operations.ListEventsAsync(secret, Scope, TimeSpan.FromHours(1));
                return events.Any(IsCrasherBackOff);
            }, TimeSpan.FromSeconds(90));

            events.Should().OnlyContain(e => e.Namespace == Ns);
        }
    }

    private static bool IsCrasherBackOff(K8sEvent e) =>
        e is { Type: "Warning", Reason: "BackOff" } && e.Regarding.Kind == "Pod" && e.Regarding.Name == "crasher";

    private async Task CreateCrasherAsync()
    {
        await k3s.EnsureNamespaceAsync(Ns);
        try
        {
            await k3s.Admin.CoreV1.CreateNamespacedPodAsync(new V1Pod
            {
                Metadata = new V1ObjectMeta { Name = "crasher" },
                Spec = new V1PodSpec
                {
                    RestartPolicy = "Always",
                    TerminationGracePeriodSeconds = 0,
                    Containers = [new V1Container { Name = "main", Image = "busybox:1.36", Command = ["sh", "-c", "exit 1"] }],
                },
            }, Ns);
        }
        catch (HttpOperationException ex) when ((int)ex.Response.StatusCode == 409)
        {
            // Created by the other test in this fixture.
        }
    }
}

/// <summary>The events fetch and window filter without a cluster: a substituted IKubernetes.</summary>
public class EventFetchTests
{
    private static readonly DateTimeOffset Now = TestData.T0;

    private static HttpOperationException Forbidden() => new("forbidden")
    {
        Response = new HttpResponseMessageWrapper(new HttpResponseMessage(HttpStatusCode.Forbidden), ""),
    };

    private static Corev1Event CoreEvent(string ns, string type, string reason) => new()
    {
        Metadata = new V1ObjectMeta { Name = $"{reason}.1", NamespaceProperty = ns },
        InvolvedObject = new V1ObjectReference { Kind = "Pod", Name = "api-1", NamespaceProperty = ns },
        Type = type,
        Reason = reason,
        Message = "m",
        Count = 3,
        LastTimestamp = Now.UtcDateTime.AddMinutes(-1),
        Source = new V1EventSource { Component = "kubelet" },
    };

    private static (IKubernetes Client, IEventsV1Operations EventsV1, ICoreV1Operations CoreV1) Client()
    {
        var client = Substitute.For<IKubernetes>();
        var eventsV1 = Substitute.For<IEventsV1Operations>();
        var coreV1 = Substitute.For<ICoreV1Operations>();
        client.EventsV1.Returns(eventsV1);
        client.CoreV1.Returns(coreV1);
        eventsV1.ListEventForAllNamespacesWithHttpMessagesAsync(default, default, default, default, default, default, default, default, default, default,
            default, default, default, default).ThrowsAsyncForAnyArgs(Forbidden());
        eventsV1.ListNamespacedEventWithHttpMessagesAsync(default!, default, default, default, default, default, default, default, default, default,
            default, default, default, default, default).ThrowsAsyncForAnyArgs(Forbidden());
        return (client, eventsV1, coreV1);
    }

    private static int Calls(object substitute, string method) =>
        substitute.ReceivedCalls().Count(c => c.GetMethodInfo().Name == method);

    private static string? FieldSelector(object substitute, string method) =>
        substitute.ReceivedCalls().Single(c => c.GetMethodInfo().Name == method)
            .GetMethodInfo().GetParameters().Zip(substitute.ReceivedCalls().Single(c => c.GetMethodInfo().Name == method).GetArguments())
            .Single(p => p.First.Name == "fieldSelector").Second as string;

    private static IReadOnlyList<string?> FieldSelectors(object substitute, string method) =>
        substitute.ReceivedCalls().Where(c => c.GetMethodInfo().Name == method)
            .Select(c => c.GetMethodInfo().GetParameters().Zip(c.GetArguments()).Single(p => p.First.Name == "fieldSelector").Second as string)
            .ToList();

    private static void CoreNamespacedReturns(ICoreV1Operations coreV1) =>
        coreV1.ListNamespacedEventWithHttpMessagesAsync(default!, default, default, default, default, default, default, default, default, default,
                default, default, default, default, default)
            .ReturnsForAnyArgs(ci => new HttpOperationResponse<Corev1EventList>
            {
                Body = new Corev1EventList { Items = [CoreEvent((string)ci[0], "Warning", "BackOff")] },
            });

    [Fact]
    public async Task All_namespaces_lists_cluster_wide_and_the_first_403_falls_back_to_core_v1_cluster_wide()
    {
        var (client, eventsV1, coreV1) = Client();
        coreV1.ListEventForAllNamespacesWithHttpMessagesAsync(default, default, default, default, default, default, default, default, default, default,
                default, default, default, default)
            .ReturnsForAnyArgs(new HttpOperationResponse<Corev1EventList>
            {
                Body = new Corev1EventList { Items = [CoreEvent("checkout", "Warning", "BackOff")] },
            });
        using var gate = new SemaphoreSlim(KubernetesOperations.MaxNamespaceFanOut);

        var events = await KubernetesOperations.FetchEventsAsync(client, [], null, gate, NullLogger.Instance, CancellationToken.None);

        events.Should().ContainSingle().Which.Should().Be(new K8sEvent("checkout", "Warning", "BackOff", "m",
            new K8sRef("Pod", "checkout", "api-1"), 3, Now.AddMinutes(-1), "kubelet"));
        Calls(eventsV1, nameof(IEventsV1Operations.ListEventForAllNamespacesWithHttpMessagesAsync)).Should().Be(1);
        Calls(eventsV1, nameof(IEventsV1Operations.ListNamespacedEventWithHttpMessagesAsync)).Should().Be(0);
        Calls(coreV1, nameof(ICoreV1Operations.ListEventForAllNamespacesWithHttpMessagesAsync)).Should().Be(1);
        Calls(coreV1, nameof(ICoreV1Operations.ListNamespacedEventWithHttpMessagesAsync)).Should().Be(0);
        FieldSelector(coreV1, nameof(ICoreV1Operations.ListEventForAllNamespacesWithHttpMessagesAsync)).Should().BeNull();
    }

    [Fact]
    public async Task Selected_namespaces_are_listed_per_namespace_never_cluster_wide()
    {
        var (client, eventsV1, coreV1) = Client();
        eventsV1.ListNamespacedEventWithHttpMessagesAsync(default!, default, default, default, default, default, default, default, default, default,
                default, default, default, default, default)
            .ReturnsForAnyArgs(ci => new HttpOperationResponse<Eventsv1EventList>
            {
                Body = new Eventsv1EventList
                {
                    Items =
                    [
                        new Eventsv1Event
                        {
                            Metadata = new V1ObjectMeta { Name = "e", NamespaceProperty = (string)ci[0] },
                            Regarding = new V1ObjectReference { Kind = "Pod", Name = "api-1", NamespaceProperty = (string)ci[0] },
                            Type = "Normal", Reason = "Pulled", Note = "n", EventTime = Now.UtcDateTime,
                        },
                    ],
                },
            });
        using var gate = new SemaphoreSlim(KubernetesOperations.MaxNamespaceFanOut);

        var events = await KubernetesOperations.FetchEventsAsync(client, ["checkout", "payments"], null, gate,
            NullLogger.Instance, CancellationToken.None);

        events.Select(e => e.Namespace).Should().BeEquivalentTo(["checkout", "payments"]);
        Calls(eventsV1, nameof(IEventsV1Operations.ListEventForAllNamespacesWithHttpMessagesAsync)).Should().Be(0);
        Calls(eventsV1, nameof(IEventsV1Operations.ListNamespacedEventWithHttpMessagesAsync)).Should().Be(2);
        Calls(coreV1, nameof(ICoreV1Operations.ListEventForAllNamespacesWithHttpMessagesAsync)).Should().Be(0);
        Calls(coreV1, nameof(ICoreV1Operations.ListNamespacedEventWithHttpMessagesAsync)).Should().Be(0);
    }

    [Fact]
    public async Task Selected_namespaces_fall_back_to_core_v1_per_namespace_with_the_type_selector()
    {
        var (client, eventsV1, coreV1) = Client();
        CoreNamespacedReturns(coreV1);
        using var gate = new SemaphoreSlim(KubernetesOperations.MaxNamespaceFanOut);

        var events = await KubernetesOperations.FetchEventsAsync(client, ["checkout", "payments"], "Warning", gate,
            NullLogger.Instance, CancellationToken.None);

        events.Select(e => e.Namespace).Should().BeEquivalentTo(["checkout", "payments"]);
        FieldSelectors(eventsV1, nameof(IEventsV1Operations.ListNamespacedEventWithHttpMessagesAsync)).Should().OnlyContain(f => f == "type=Warning");
        FieldSelectors(coreV1, nameof(ICoreV1Operations.ListNamespacedEventWithHttpMessagesAsync)).Should().Equal("type=Warning", "type=Warning");
        Calls(eventsV1, nameof(IEventsV1Operations.ListEventForAllNamespacesWithHttpMessagesAsync)).Should().Be(0);
        Calls(coreV1, nameof(ICoreV1Operations.ListEventForAllNamespacesWithHttpMessagesAsync)).Should().Be(0);
    }

    [Fact]
    public async Task Object_events_use_one_regarding_selector_per_object()
    {
        var (client, eventsV1, _) = Client();
        eventsV1.ListNamespacedEventWithHttpMessagesAsync(default!, default, default, default, default, default, default, default, default, default,
                default, default, default, default, default)
            .ReturnsForAnyArgs(new HttpOperationResponse<Eventsv1EventList> { Body = new Eventsv1EventList { Items = [] } });

        await KubernetesOperations.ObjectEventsAsync(client, "checkout", [("Deployment", "web"), ("ReplicaSet", "web-1"), ("Pod", "web-1-a")],
            NullLogger.Instance, CancellationToken.None);

        FieldSelectors(eventsV1, nameof(IEventsV1Operations.ListNamespacedEventWithHttpMessagesAsync)).Should().BeEquivalentTo(
            "regarding.kind=Deployment,regarding.name=web", "regarding.kind=ReplicaSet,regarding.name=web-1", "regarding.kind=Pod,regarding.name=web-1-a");
    }

    [Fact]
    public async Task Object_events_fall_back_to_core_v1_involved_object_selectors()
    {
        var (client, _, coreV1) = Client();
        CoreNamespacedReturns(coreV1);

        var events = await KubernetesOperations.ObjectEventsAsync(client, "checkout", [("Pod", "api-1")], NullLogger.Instance, CancellationToken.None);

        events.Should().ContainSingle().Which.Regarding.Should().Be(new K8sRef("Pod", "checkout", "api-1"));
        FieldSelectors(coreV1, nameof(ICoreV1Operations.ListNamespacedEventWithHttpMessagesAsync))
            .Should().Equal("involvedObject.kind=Pod,involvedObject.name=api-1");
    }

    [Fact]
    public async Task Object_events_are_empty_when_both_event_apis_are_forbidden()
    {
        var (client, _, coreV1) = Client();
        coreV1.ListNamespacedEventWithHttpMessagesAsync(default!, default, default, default, default, default, default, default, default, default,
            default, default, default, default, default).ThrowsAsyncForAnyArgs(Forbidden());

        var events = await KubernetesOperations.ObjectEventsAsync(client, "checkout", [("Pod", "api-1")], NullLogger.Instance, CancellationToken.None);

        events.Should().BeEmpty();
    }

    [Fact]
    public void Window_keeps_recent_events_in_scope_newest_first()
    {
        K8sEvent Ev(string? ns, int minutesAgo) =>
            new(ns, "Warning", "BackOff", "m", new K8sRef("Pod", ns, $"p{minutesAgo}"), 1, Now.AddMinutes(-minutesAgo), "kubelet");
        var events = new[] { Ev("checkout", 30), Ev("checkout", 61), Ev("checkout", 5), Ev("payments", 1), Ev("checkout", 60), Ev(null, 2) };

        var scoped = KubernetesOperations.InWindow(events, ["checkout"], Now, TimeSpan.FromHours(1));
        scoped.Select(e => e.Regarding.Name).Should().Equal("p5", "p30", "p60");

        var all = KubernetesOperations.InWindow(events, [], Now, TimeSpan.FromHours(1));
        all.Select(e => e.Regarding.Name).Should().Equal("p1", "p2", "p5", "p30", "p60");
    }
}
