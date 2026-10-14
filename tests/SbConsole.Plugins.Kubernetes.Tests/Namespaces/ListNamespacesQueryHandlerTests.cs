using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Plugins.Kubernetes.Namespaces;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes.Tests.Namespaces;

public class ListNamespacesQueryHandlerTests
{
    private static readonly Guid Id = Guid.NewGuid();
    private readonly IKubernetesOperations _ops = Substitute.For<IKubernetesOperations>();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();

    private ListNamespacesQueryHandler Sut() => new(_ops, _connections, NullLogger<ListNamespacesQueryHandler>.Instance);

    [Fact]
    public async Task Returns_the_namespaces_with_pod_counts()
    {
        _connections.GetSecretAsync(Id, Arg.Any<CancellationToken>()).Returns("secret");
        _ops.ListNamespacesAsync("secret", Arg.Any<CancellationToken>()).Returns(TestData.Namespaces());

        var result = await Sut().HandleAsync(Id);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Select(n => n.Name).Should().Equal("checkout", "payments", "payments-canary");
    }

    [Fact]
    public async Task A_failure_is_a_friendly_error()
    {
        _connections.GetSecretAsync(Id, Arg.Any<CancellationToken>()).Returns("secret");
        _ops.ListNamespacesAsync("secret", Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("boom"));

        var result = await Sut().HandleAsync(Id);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_missing_connection_is_not_found()
    {
        _connections.GetSecretAsync(Id, Arg.Any<CancellationToken>()).Returns((string?)null);

        (await Sut().HandleAsync(Id)).Error.Should().Be("Connection not found.");
    }
}
