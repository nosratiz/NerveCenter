using System.Net;
using FluentAssertions;
using k8s.Autorest;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Plugins.Kubernetes.Deployments;
using SbConsole.Plugins.Kubernetes.Workloads;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes.Tests.Deployments;

public class DeploymentHandlerTests
{
    private const string Secret = "secret";
    private readonly IKubernetesOperations _ops = Substitute.For<IKubernetesOperations>();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IAuditScope _audit = Substitute.For<IAuditScope>();
    private readonly Guid _id = Guid.NewGuid();

    public DeploymentHandlerTests() =>
        _connections.GetSecretAsync(_id, Arg.Any<CancellationToken>()).Returns(Secret);

    private K8sTarget Target => new(_id, "aks-prod-eu", "checkout", "payment-gateway");

    private GetDeploymentDetailQueryHandler Detail() => new(_ops, _connections, NullLogger<GetDeploymentDetailQueryHandler>.Instance);
    private RollbackDeploymentCommandHandler Rollback() => new(_ops, _connections, _audit, NullLogger<RollbackDeploymentCommandHandler>.Instance);

    [Fact]
    public async Task Detail_returns_the_deployment()
    {
        var detail = TestData.PaymentGatewayDetail();
        _ops.GetDeploymentDetailAsync(Secret, "checkout", "payment-gateway", Arg.Any<CancellationToken>()).Returns(detail);

        var result = await Detail().HandleAsync(_id, "checkout", "payment-gateway");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeSameAs(detail);
    }

    [Fact]
    public async Task Detail_of_a_missing_deployment_is_a_successful_null()
    {
        _ops.GetDeploymentDetailAsync(Secret, "checkout", "x", Arg.Any<CancellationToken>()).Returns((K8sDeploymentDetail?)null);

        var result = await Detail().HandleAsync(_id, "checkout", "x");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task Detail_failure_is_friendly()
    {
        _ops.GetDeploymentDetailAsync(Secret, "checkout", "payment-gateway", Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpOperationException("raw") { Response = new HttpResponseMessageWrapper(new HttpResponseMessage(HttpStatusCode.Unauthorized), "") });

        var result = await Detail().HandleAsync(_id, "checkout", "payment-gateway");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("The cluster rejected the credentials, or they have expired.");
    }

    [Fact]
    public async Task Rollback_audits_from_and_to_revision()
    {
        _ops.RollbackDeploymentAsync(Secret, "checkout", "payment-gateway", 22, Arg.Any<CancellationToken>()).Returns(23L);

        var result = await Rollback().HandleAsync(new RollbackCommand(Target, 22));

        result.IsSuccess.Should().BeTrue();
        await _audit.Received(1).RecordAsync("k8s.deployment.rollback", "aks-prod-eu/checkout/payment-gateway",
            ActionRisk.Mutating, true, "rev 23 → rev 22", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rollback_to_a_missing_revision_fails_with_its_message_and_is_audited_as_failed()
    {
        _ops.RollbackDeploymentAsync(Secret, "checkout", "payment-gateway", 3, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Revision 3 no longer exists."));

        var result = await Rollback().HandleAsync(new RollbackCommand(Target, 3));

        result.Error.Should().Be("Revision 3 no longer exists.");
        await _audit.Received(1).RecordAsync("k8s.deployment.rollback", "aks-prod-eu/checkout/payment-gateway",
            ActionRisk.Mutating, false, "Revision 3 no longer exists.", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rollback_with_an_unknown_connection_is_not_audited()
    {
        var unknown = Guid.NewGuid();
        _connections.GetSecretAsync(unknown, Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await Rollback().HandleAsync(new RollbackCommand(Target with { ConnectionId = unknown }, 22));

        result.Error.Should().Be("Connection not found.");
        await _audit.DidNotReceiveWithAnyArgs().RecordAsync(default!, default!, default, default, default, default);
    }
}
