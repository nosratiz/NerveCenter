using System.Net;
using FluentAssertions;
using k8s.Autorest;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Plugins.Kubernetes.Workloads;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes.Tests.Workloads;

public class WorkloadHandlerTests
{
    private const string Secret = "secret";
    private readonly IKubernetesOperations _ops = Substitute.For<IKubernetesOperations>();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IAuditScope _audit = Substitute.For<IAuditScope>();
    private readonly FakeTimeProvider _time = new(TestData.T0);
    private readonly Guid _id = Guid.NewGuid();

    public WorkloadHandlerTests() =>
        _connections.GetSecretAsync(_id, Arg.Any<CancellationToken>()).Returns(Secret);

    private K8sTarget Target => new(_id, "aks-prod-eu", "checkout", "payment-gateway");

    private ScaleWorkloadCommandHandler Scale() => new(_ops, _connections, _audit, NullLogger<ScaleWorkloadCommandHandler>.Instance);
    private RestartWorkloadCommandHandler Restart() => new(_ops, _connections, _audit, _time, NullLogger<RestartWorkloadCommandHandler>.Instance);
    private DeleteWorkloadCommandHandler Delete() => new(_ops, _connections, _audit, NullLogger<DeleteWorkloadCommandHandler>.Instance);

    private static HttpOperationException Forbidden() => new("raw")
    {
        Response = new HttpResponseMessageWrapper(new HttpResponseMessage(HttpStatusCode.Forbidden),
            "{\"kind\":\"Status\",\"message\":\"User \\\"x\\\" cannot patch resource \\\"deployments/scale\\\" in API group \\\"apps\\\" in the namespace \\\"checkout\\\"\"}"),
    };

    private Task NotAudited() => _audit.DidNotReceiveWithAnyArgs().RecordAsync(default!, default!, default, default, default, default);

    [Fact]
    public async Task Scaling_a_deployment_audits_from_and_to_replicas()
    {
        _ops.ScaleAsync(Secret, K8sWorkloadKind.Deployment, "checkout", "payment-gateway", 6, Arg.Any<CancellationToken>()).Returns(4);

        var result = await Scale().HandleAsync(new ScaleCommand(Target, K8sWorkloadKind.Deployment, 6));

        result.IsSuccess.Should().BeTrue();
        await _audit.Received(1).RecordAsync("k8s.deployment.scale", "aks-prod-eu/checkout/payment-gateway",
            ActionRisk.Mutating, true, "4 → 6 replicas", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Scaling_a_statefulset_uses_its_own_action()
    {
        _ops.ScaleAsync(Secret, K8sWorkloadKind.StatefulSet, "checkout", "payment-gateway", 2, Arg.Any<CancellationToken>()).Returns(3);

        var result = await Scale().HandleAsync(new ScaleCommand(Target, K8sWorkloadKind.StatefulSet, 2));

        result.IsSuccess.Should().BeTrue();
        await _audit.Received(1).RecordAsync("k8s.statefulset.scale", "aks-prod-eu/checkout/payment-gateway",
            ActionRisk.Mutating, true, "3 → 2 replicas", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(K8sWorkloadKind.DaemonSet)]
    [InlineData(K8sWorkloadKind.Job)]
    [InlineData(K8sWorkloadKind.CronJob)]
    public async Task Scaling_other_kinds_is_refused_and_not_audited(K8sWorkloadKind kind)
    {
        var result = await Scale().HandleAsync(new ScaleCommand(Target, kind, 2));

        result.Error.Should().Be("Only Deployments and StatefulSets can be scaled.");
        await NotAudited();
        await _ops.DidNotReceiveWithAnyArgs().ScaleAsync(default!, default, default!, default!, default, default);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1001)]
    public async Task Out_of_range_replicas_are_refused_and_not_audited(int replicas)
    {
        var result = await Scale().HandleAsync(new ScaleCommand(Target, K8sWorkloadKind.Deployment, replicas));

        result.Error.Should().Be("Replicas must be between 0 and 1000.");
        await NotAudited();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    public async Task Boundary_replicas_are_accepted(int replicas)
    {
        var result = await Scale().HandleAsync(new ScaleCommand(Target, K8sWorkloadKind.Deployment, replicas));
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task A_forbidden_scale_is_audited_as_failed_with_the_friendly_sentence()
    {
        _ops.ScaleAsync(Secret, K8sWorkloadKind.Deployment, "checkout", "payment-gateway", 6, Arg.Any<CancellationToken>())
            .ThrowsAsync(Forbidden());

        var result = await Scale().HandleAsync(new ScaleCommand(Target, K8sWorkloadKind.Deployment, 6));

        result.Error.Should().Be("Your account can't patch deployments/scale in checkout.");
        await _audit.Received(1).RecordAsync("k8s.deployment.scale", "aks-prod-eu/checkout/payment-gateway",
            ActionRisk.Mutating, false, "Your account can't patch deployments/scale in checkout.", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(K8sWorkloadKind.Deployment, "k8s.deployment.restart")]
    [InlineData(K8sWorkloadKind.StatefulSet, "k8s.statefulset.restart")]
    [InlineData(K8sWorkloadKind.DaemonSet, "k8s.daemonset.restart")]
    public async Task Restart_patches_with_the_clock_and_audits_mutating(K8sWorkloadKind kind, string action)
    {
        var result = await Restart().HandleAsync(new WorkloadCommand(Target, kind));

        result.IsSuccess.Should().BeTrue();
        await _ops.Received(1).RestartAsync(Secret, kind, "checkout", "payment-gateway", TestData.T0, Arg.Any<CancellationToken>());
        await _audit.Received(1).RecordAsync(action, "aks-prod-eu/checkout/payment-gateway",
            ActionRisk.Mutating, true, "restartedAt 2026-10-14T12:00:00.0000000Z", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(K8sWorkloadKind.Job)]
    [InlineData(K8sWorkloadKind.CronJob)]
    public async Task Restarting_jobs_is_refused_and_not_audited(K8sWorkloadKind kind)
    {
        var result = await Restart().HandleAsync(new WorkloadCommand(Target, kind));

        result.Error.Should().Be("Only Deployments, StatefulSets and DaemonSets can be restarted.");
        await NotAudited();
    }

    [Fact]
    public async Task A_failed_restart_is_audited_as_failed()
    {
        _ops.RestartAsync(default!, default, default!, default!, default, default).ThrowsAsyncForAnyArgs(Forbidden());

        var result = await Restart().HandleAsync(new WorkloadCommand(Target, K8sWorkloadKind.Deployment));

        result.IsSuccess.Should().BeFalse();
        await _audit.Received(1).RecordAsync("k8s.deployment.restart", "aks-prod-eu/checkout/payment-gateway",
            ActionRisk.Mutating, false, result.Error, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(K8sWorkloadKind.Deployment, "k8s.deployment.delete")]
    [InlineData(K8sWorkloadKind.StatefulSet, "k8s.statefulset.delete")]
    [InlineData(K8sWorkloadKind.DaemonSet, "k8s.daemonset.delete")]
    [InlineData(K8sWorkloadKind.Job, "k8s.job.delete")]
    [InlineData(K8sWorkloadKind.CronJob, "k8s.cronjob.delete")]
    public async Task Delete_audits_destructive(K8sWorkloadKind kind, string action)
    {
        var result = await Delete().HandleAsync(new WorkloadCommand(Target, kind));

        result.IsSuccess.Should().BeTrue();
        await _ops.Received(1).DeleteWorkloadAsync(Secret, kind, "checkout", "payment-gateway", Arg.Any<CancellationToken>());
        await _audit.Received(1).RecordAsync(action, "aks-prod-eu/checkout/payment-gateway",
            ActionRisk.Destructive, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_delete_is_audited_as_failed_with_the_friendly_sentence()
    {
        _ops.DeleteWorkloadAsync(default!, default, default!, default!, default).ThrowsAsyncForAnyArgs(Forbidden());

        var result = await Delete().HandleAsync(new WorkloadCommand(Target, K8sWorkloadKind.Deployment));

        result.Error.Should().Be("Your account can't patch deployments/scale in checkout.");
        await _audit.Received(1).RecordAsync("k8s.deployment.delete", "aks-prod-eu/checkout/payment-gateway",
            ActionRisk.Destructive, false, result.Error, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_missing_connection_is_not_audited()
    {
        var target = Target with { ConnectionId = Guid.NewGuid() };
        _connections.GetSecretAsync(target.ConnectionId, Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await Delete().HandleAsync(new WorkloadCommand(target, K8sWorkloadKind.Deployment));

        result.Error.Should().Be(HandlerRunner.ConnectionNotFound);
        await NotAudited();
    }

    [Fact]
    public async Task List_passes_kind_namespaces_and_selector_through()
    {
        IReadOnlyList<string> namespaces = ["checkout", "payments"];
        IReadOnlyList<K8sWorkload> workloads = [TestData.Deployment()];
        _ops.ListWorkloadsAsync(Secret, K8sWorkloadKind.StatefulSet, namespaces, "app=web", Arg.Any<CancellationToken>()).Returns(workloads);
        var handler = new ListWorkloadsQueryHandler(_ops, _connections, NullLogger<ListWorkloadsQueryHandler>.Instance);

        var result = await handler.HandleAsync(_id, K8sWorkloadKind.StatefulSet, namespaces, "app=web");

        result.Value.Should().BeSameAs(workloads);
    }

    [Fact]
    public async Task List_failure_is_a_friendly_error()
    {
        _ops.ListWorkloadsAsync(default!, default, default!, default, default).ThrowsAsyncForAnyArgs(Forbidden());
        var handler = new ListWorkloadsQueryHandler(_ops, _connections, NullLogger<ListWorkloadsQueryHandler>.Instance);

        var result = await handler.HandleAsync(_id, K8sWorkloadKind.Deployment, [], null);

        result.Error.Should().Be("Your account can't patch deployments/scale in checkout.");
    }

    [Fact]
    public async Task Count_passes_namespaces_through()
    {
        IReadOnlyList<string> namespaces = ["checkout"];
        IReadOnlyDictionary<K8sWorkloadKind, K8sKindCount> counts = new Dictionary<K8sWorkloadKind, K8sKindCount>
        {
            [K8sWorkloadKind.Deployment] = new(3, true),
        };
        _ops.CountWorkloadsAsync(Secret, namespaces, Arg.Any<CancellationToken>()).Returns(counts);
        var handler = new CountWorkloadsQueryHandler(_ops, _connections, NullLogger<CountWorkloadsQueryHandler>.Instance);

        (await handler.HandleAsync(_id, namespaces)).Value.Should().BeSameAs(counts);
    }

    [Fact]
    public async Task Scale_info_passes_through()
    {
        var info = new K8sScaleInfo(4, 4, true, "pg-hpa", 250, 512L * 1024 * 1024);
        _ops.GetScaleInfoAsync(Secret, K8sWorkloadKind.Deployment, "checkout", "payment-gateway", Arg.Any<CancellationToken>()).Returns(info);
        var handler = new GetScaleInfoQueryHandler(_ops, _connections, NullLogger<GetScaleInfoQueryHandler>.Instance);

        (await handler.HandleAsync(_id, K8sWorkloadKind.Deployment, "checkout", "payment-gateway")).Value.Should().Be(info);
    }

    [Fact]
    public async Task Yaml_passes_through_including_not_found()
    {
        _ops.GetYamlAsync(Secret, K8sResourceKind.Node, null, "node-1", Arg.Any<CancellationToken>()).Returns("kind: Node\n");
        _ops.GetYamlAsync(Secret, K8sResourceKind.Pod, "checkout", "gone", Arg.Any<CancellationToken>()).Returns((string?)null);
        var handler = new GetResourceYamlQueryHandler(_ops, _connections, NullLogger<GetResourceYamlQueryHandler>.Instance);

        (await handler.HandleAsync(_id, K8sResourceKind.Node, null, "node-1")).Value.Should().Be("kind: Node\n");
        var missing = await handler.HandleAsync(_id, K8sResourceKind.Pod, "checkout", "gone");
        missing.IsSuccess.Should().BeTrue();
        missing.Value.Should().BeNull();
    }
}
