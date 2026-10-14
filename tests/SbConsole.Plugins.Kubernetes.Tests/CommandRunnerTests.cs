using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes.Tests;

public class CommandRunnerTests
{
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IAuditScope _audit = Substitute.For<IAuditScope>();
    private readonly ILogger _logger = Substitute.For<ILogger>();
    private readonly Guid _id = Guid.NewGuid();

    private Task<PluginResult> Run(Func<string, Task<string?>> work, CancellationToken ct = default) =>
        CommandRunner.RunAsync(_connections, _audit, _logger, _id, "k8s.pod.delete", "c/ns/p", ActionRisk.Destructive, work, ct);

    [Fact]
    public async Task A_missing_connection_is_not_audited()
    {
        _connections.GetSecretAsync(_id, Arg.Any<CancellationToken>()).Returns((string?)null);
        var result = await Run(_ => Task.FromResult<string?>(null));
        result.Error.Should().Be(HandlerRunner.ConnectionNotFound);
        await _audit.DidNotReceiveWithAnyArgs().RecordAsync(default!, default!, default, default, default, default);
    }

    [Fact]
    public async Task Success_audits_the_detail()
    {
        _connections.GetSecretAsync(_id, Arg.Any<CancellationToken>()).Returns("s");
        var result = await Run(s => Task.FromResult<string?>("detail " + s));
        result.IsSuccess.Should().BeTrue();
        await _audit.Received(1).RecordAsync("k8s.pod.delete", "c/ns/p", ActionRisk.Destructive, true, "detail s", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Failure_audits_the_friendly_message()
    {
        _connections.GetSecretAsync(_id, Arg.Any<CancellationToken>()).Returns("s");
        var result = await Run(_ => throw new TimeoutException());
        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Can't reach the cluster — it didn't answer in time.");
        await _audit.Received(1).RecordAsync("k8s.pod.delete", "c/ns/p", ActionRisk.Destructive, false, result.Error, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Caller_cancellation_is_rethrown()
    {
        _connections.GetSecretAsync(_id, Arg.Any<CancellationToken>()).Returns("s");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await FluentActions.Awaiting(() => Run(_ => throw new OperationCanceledException(cts.Token), cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task An_audit_write_failure_is_logged_not_thrown()
    {
        _connections.GetSecretAsync(_id, Arg.Any<CancellationToken>()).Returns("s");
        _audit.RecordAsync(default!, default!, default, default, default, default)
            .ThrowsForAnyArgs(new InvalidOperationException("db down"));
        var result = await Run(_ => Task.FromResult<string?>("ok"));
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task The_generic_overload_returns_the_value()
    {
        _connections.GetSecretAsync(_id, Arg.Any<CancellationToken>()).Returns("s");
        var result = await CommandRunner.RunAsync(_connections, _audit, _logger, _id, "a", "t", ActionRisk.Mutating,
            _ => Task.FromResult((Value: 7, Detail: (string?)"d")), CancellationToken.None);
        result.Value.Should().Be(7);
        await _audit.Received(1).RecordAsync("a", "t", ActionRisk.Mutating, true, "d", Arg.Any<CancellationToken>());
    }
}
