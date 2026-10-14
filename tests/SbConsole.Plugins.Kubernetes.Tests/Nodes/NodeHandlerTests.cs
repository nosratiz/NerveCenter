using System.Net;
using FluentAssertions;
using k8s.Autorest;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Plugins.Kubernetes.Nodes;
using SbConsole.Plugins.Kubernetes.Workloads;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes.Tests.Nodes;

public class NodeHandlerTests
{
    private const string Secret = "secret";
    private const string Node = "aks-pool1-38211-vmss00000a";
    private readonly IKubernetesOperations _ops = Substitute.For<IKubernetesOperations>();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IAuditScope _audit = Substitute.For<IAuditScope>();
    private readonly Guid _id = Guid.NewGuid();

    public NodeHandlerTests() =>
        _connections.GetSecretAsync(_id, Arg.Any<CancellationToken>()).Returns(Secret);

    private K8sTarget Target => new(_id, "aks-prod-eu", null, Node);

    private SetNodeSchedulableCommandHandler SetSchedulable() =>
        new(_ops, _connections, _audit, NullLogger<SetNodeSchedulableCommandHandler>.Instance);

    private ListNodesQueryHandler ListNodes() => new(_ops, _connections, NullLogger<ListNodesQueryHandler>.Instance);

    private ListNodePodsQueryHandler ListPods() => new(_ops, _connections, NullLogger<ListNodePodsQueryHandler>.Instance);

    private static HttpOperationException Forbidden(string verb, string resource) => new("raw")
    {
        Response = new HttpResponseMessageWrapper(new HttpResponseMessage(HttpStatusCode.Forbidden),
            $"{{\"kind\":\"Status\",\"message\":\"{resource} is forbidden: User \\\"x\\\" cannot {verb} resource \\\"{resource}\\\" in API group \\\"\\\" at the cluster scope\"}}"),
    };

    [Fact]
    public async Task Cordon_marks_the_node_unschedulable_and_audits_k8s_node_cordon()
    {
        var result = await SetSchedulable().HandleAsync(new SetSchedulableCommand(Target, Unschedulable: true));

        result.IsSuccess.Should().BeTrue();
        await _ops.Received(1).SetUnschedulableAsync(Secret, Node, true, Arg.Any<CancellationToken>());
        await _audit.Received(1).RecordAsync("k8s.node.cordon", $"aks-prod-eu/{Node}", ActionRisk.Mutating, true, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Uncordon_clears_unschedulable_and_audits_k8s_node_uncordon()
    {
        var result = await SetSchedulable().HandleAsync(new SetSchedulableCommand(Target, Unschedulable: false));

        result.IsSuccess.Should().BeTrue();
        await _ops.Received(1).SetUnschedulableAsync(Secret, Node, false, Arg.Any<CancellationToken>());
        await _audit.Received(1).RecordAsync("k8s.node.uncordon", $"aks-prod-eu/{Node}", ActionRisk.Mutating, true, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_forbidden_cordon_is_audited_as_failed_with_the_friendly_message()
    {
        _ops.SetUnschedulableAsync(Secret, Node, true, Arg.Any<CancellationToken>()).ThrowsAsync(Forbidden("patch", "nodes"));

        var result = await SetSchedulable().HandleAsync(new SetSchedulableCommand(Target, true));

        result.Error.Should().Be("Your account can't patch nodes.");
        await _audit.Received(1).RecordAsync("k8s.node.cordon", $"aks-prod-eu/{Node}", ActionRisk.Mutating, false,
            "Your account can't patch nodes.", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unknown_connection_is_not_audited()
    {
        var unknown = Guid.NewGuid();
        _connections.GetSecretAsync(unknown, Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await SetSchedulable().HandleAsync(new SetSchedulableCommand(Target with { ConnectionId = unknown }, true));

        result.Error.Should().Be("Connection not found.");
        await _audit.DidNotReceiveWithAnyArgs().RecordAsync(default!, default!, default, default, default, default);
    }

    [Fact]
    public async Task List_nodes_returns_the_operations_rows()
    {
        var nodes = TestData.Nodes();
        _ops.ListNodesAsync(Secret, Arg.Any<CancellationToken>()).Returns(nodes);

        var result = await ListNodes().HandleAsync(_id);

        result.Value.Should().BeSameAs(nodes);
    }

    [Fact]
    public async Task List_nodes_forbidden_says_the_account_cant_list_nodes()
    {
        _ops.ListNodesAsync(Secret, Arg.Any<CancellationToken>()).ThrowsAsync(Forbidden("list", "nodes"));

        var result = await ListNodes().HandleAsync(_id);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Your account can't list nodes.");
    }

    [Fact]
    public async Task List_node_pods_asks_for_that_node()
    {
        IReadOnlyList<K8sPodSummary> pods = [TestData.Pod(node: Node)];
        _ops.ListPodsOnNodeAsync(Secret, Node, Arg.Any<CancellationToken>()).Returns(pods);

        var result = await ListPods().HandleAsync(_id, Node);

        result.Value.Should().BeSameAs(pods);
    }
}
