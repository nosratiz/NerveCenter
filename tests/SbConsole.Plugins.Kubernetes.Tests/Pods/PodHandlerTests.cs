using System.Net;
using FluentAssertions;
using k8s.Autorest;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Plugins.Kubernetes.Pods;
using SbConsole.Plugins.Kubernetes.Workloads;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes.Tests.Pods;

public class PodHandlerTests
{
    private const string Secret = "secret";
    private const string Ns = "checkout";
    private const string PodName = "payment-gateway-7d9f8-x2k4q";
    private readonly IKubernetesOperations _ops = Substitute.For<IKubernetesOperations>();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IAuditScope _audit = Substitute.For<IAuditScope>();
    private readonly Guid _id = Guid.NewGuid();

    public PodHandlerTests() =>
        _connections.GetSecretAsync(_id, Arg.Any<CancellationToken>()).Returns(Secret);

    private K8sTarget Target => new(_id, "aks-prod-eu", Ns, PodName);

    private GetPodDetailQueryHandler Detail() => new(_ops, _connections, NullLogger<GetPodDetailQueryHandler>.Instance);
    private DeletePodCommandHandler Delete() => new(_ops, _connections, _audit, NullLogger<DeletePodCommandHandler>.Instance);

    private static HttpOperationException Http(HttpStatusCode code) =>
        new("raw") { Response = new HttpResponseMessageWrapper(new HttpResponseMessage(code), "") };

    [Fact]
    public async Task Detail_returns_the_pod()
    {
        var detail = TestData.PaymentGatewayPodDetail();
        _ops.GetPodDetailAsync(Secret, Ns, PodName, Arg.Any<CancellationToken>()).Returns(detail);

        var result = await Detail().HandleAsync(_id, Ns, PodName);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeSameAs(detail);
    }

    [Fact]
    public async Task Detail_of_a_missing_pod_is_a_successful_null()
    {
        _ops.GetPodDetailAsync(Secret, Ns, "x", Arg.Any<CancellationToken>()).Returns((K8sPodDetail?)null);

        var result = await Detail().HandleAsync(_id, Ns, "x");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task Detail_failure_is_friendly()
    {
        _ops.GetPodDetailAsync(Secret, Ns, PodName, Arg.Any<CancellationToken>()).ThrowsAsync(Http(HttpStatusCode.Unauthorized));

        var result = await Detail().HandleAsync(_id, Ns, PodName);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("The cluster rejected the credentials, or they have expired.");
    }

    [Fact]
    public async Task Delete_deletes_and_audits_destructive_with_the_owner()
    {
        _ops.GetPodDetailAsync(Secret, Ns, PodName, Arg.Any<CancellationToken>()).Returns(TestData.PaymentGatewayPodDetail());

        var result = await Delete().HandleAsync(new DeletePodCommand(Target));

        result.IsSuccess.Should().BeTrue();
        await _ops.Received(1).DeletePodAsync(Secret, Ns, PodName, null, Arg.Any<CancellationToken>());
        await _audit.Received(1).RecordAsync("k8s.pod.delete", "aks-prod-eu/checkout/payment-gateway-7d9f8-x2k4q",
            ActionRisk.Destructive, true, "owner Deployment/payment-gateway", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_of_a_bare_pod_has_no_detail()
    {
        var d = TestData.PaymentGatewayPodDetail();
        _ops.GetPodDetailAsync(Secret, Ns, PodName, Arg.Any<CancellationToken>())
            .Returns(d with { Pod = d.Pod with { Owner = null }, OwnerChain = [] });

        await Delete().HandleAsync(new DeletePodCommand(Target));

        await _audit.Received(1).RecordAsync("k8s.pod.delete", Target.AuditTarget, ActionRisk.Destructive, true, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_of_a_pod_that_is_already_gone_fails_and_is_audited_as_failed()
    {
        _ops.GetPodDetailAsync(Secret, Ns, PodName, Arg.Any<CancellationToken>()).Returns((K8sPodDetail?)null);

        var result = await Delete().HandleAsync(new DeletePodCommand(Target));

        result.Error.Should().Be("It no longer exists — refresh.");
        await _ops.DidNotReceiveWithAnyArgs().DeletePodAsync(default!, default!, default!, default, default);
        await _audit.Received(1).RecordAsync("k8s.pod.delete", Target.AuditTarget, ActionRisk.Destructive, false,
            "It no longer exists — refresh.", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_still_runs_when_the_pod_cannot_be_read()
    {
        _ops.GetPodDetailAsync(Secret, Ns, PodName, Arg.Any<CancellationToken>()).ThrowsAsync(Http(HttpStatusCode.Forbidden));

        var result = await Delete().HandleAsync(new DeletePodCommand(Target));

        result.IsSuccess.Should().BeTrue();
        await _ops.Received(1).DeletePodAsync(Secret, Ns, PodName, null, Arg.Any<CancellationToken>());
        await _audit.Received(1).RecordAsync("k8s.pod.delete", Target.AuditTarget, ActionRisk.Destructive, true, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_failure_is_friendly_and_audited_as_failed()
    {
        _ops.GetPodDetailAsync(Secret, Ns, PodName, Arg.Any<CancellationToken>()).Returns(TestData.PaymentGatewayPodDetail());
        _ops.DeletePodAsync(Secret, Ns, PodName, null, Arg.Any<CancellationToken>()).ThrowsAsync(Http(HttpStatusCode.NotFound));

        var result = await Delete().HandleAsync(new DeletePodCommand(Target));

        result.Error.Should().Be("It no longer exists — refresh.");
        await _audit.Received(1).RecordAsync("k8s.pod.delete", Target.AuditTarget, ActionRisk.Destructive, false,
            "It no longer exists — refresh.", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_with_an_unknown_connection_is_not_audited()
    {
        var unknown = Guid.NewGuid();
        _connections.GetSecretAsync(unknown, Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await Delete().HandleAsync(new DeletePodCommand(Target with { ConnectionId = unknown }));

        result.Error.Should().Be("Connection not found.");
        await _audit.DidNotReceiveWithAnyArgs().RecordAsync(default!, default!, default, default, default, default);
    }
}
