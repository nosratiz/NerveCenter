using System.Net;
using System.Runtime.CompilerServices;
using FluentAssertions;
using k8s.Autorest;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Plugins.Kubernetes.Pods;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes.Tests.Pods;

public class LogHandlerTests
{
    private const string Secret = "secret";
    private const string Ns = "checkout";
    private const string PodName = "payment-gateway-7d9f8-x2k4q";
    private static readonly K8sLogRequest Request = new("gateway", true, 500, true);
    private readonly IKubernetesOperations _ops = Substitute.For<IKubernetesOperations>();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly Guid _id = Guid.NewGuid();

    public LogHandlerTests() =>
        _connections.GetSecretAsync(_id, Arg.Any<CancellationToken>()).Returns(Secret);

    private ReadPodLogsQueryHandler Read() => new(_ops, _connections, NullLogger<ReadPodLogsQueryHandler>.Instance);
    private FollowPodLogsQueryHandler Follow() => new(_ops, _connections, NullLogger<FollowPodLogsQueryHandler>.Instance);
    private DownloadPodLogsQueryHandler Download() => new(_ops, _connections, NullLogger<DownloadPodLogsQueryHandler>.Instance);

    private static HttpOperationException Http(HttpStatusCode code) =>
        new("raw") { Response = new HttpResponseMessageWrapper(new HttpResponseMessage(code), "") };

    private static async IAsyncEnumerable<string> Lines(IEnumerable<string> lines, Exception? thenThrow = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var line in lines)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return line;
        }

        if (thenThrow is not null)
        {
            throw thenThrow;
        }
    }

    [Fact]
    public async Task Read_returns_the_lines()
    {
        _ops.ReadLogsAsync(Secret, Ns, PodName, Request, Arg.Any<CancellationToken>()).Returns(["a", "b"]);

        var result = await Read().HandleAsync(_id, Ns, PodName, Request);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Equal("a", "b");
    }

    [Fact]
    public async Task Read_failure_is_friendly()
    {
        _ops.ReadLogsAsync(Secret, Ns, PodName, Request, Arg.Any<CancellationToken>()).ThrowsAsync(Http(HttpStatusCode.Forbidden));

        var result = await Read().HandleAsync(_id, Ns, PodName, Request);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Your account isn't allowed to do that.");
    }

    [Fact]
    public async Task Read_with_an_unknown_connection_fails()
    {
        var unknown = Guid.NewGuid();
        _connections.GetSecretAsync(unknown, Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await Read().HandleAsync(unknown, Ns, PodName, Request);

        result.Error.Should().Be("Connection not found.");
    }

    [Fact]
    public async Task Follow_streams_the_lines()
    {
        var follow = Request with { Previous = false };
        _ops.FollowLogsAsync(Secret, Ns, PodName, follow, Arg.Any<CancellationToken>())
            .Returns(ci => Lines(["x", "y", "z"], ct: ci.ArgAt<CancellationToken>(4)));

        var seen = new List<string>();
        await foreach (var line in Follow().StreamAsync(_id, Ns, PodName, follow, CancellationToken.None))
        {
            seen.Add(line);
        }

        seen.Should().Equal("x", "y", "z");
    }

    [Fact]
    public async Task Follow_with_an_unknown_connection_throws_connection_not_found()
    {
        var unknown = Guid.NewGuid();
        _connections.GetSecretAsync(unknown, Arg.Any<CancellationToken>()).Returns((string?)null);
        var run = async () =>
        {
            await foreach (var _ in Follow().StreamAsync(unknown, Ns, PodName, Request, CancellationToken.None))
            {
            }
        };

        (await run.Should().ThrowAsync<InvalidOperationException>()).WithMessage("Connection not found.");
        _ops.DidNotReceiveWithAnyArgs().FollowLogsAsync(default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task Follow_propagates_stream_errors()
    {
        _ops.FollowLogsAsync(Secret, Ns, PodName, Request, Arg.Any<CancellationToken>())
            .Returns(ci => Lines(["x"], Http(HttpStatusCode.Unauthorized), ci.ArgAt<CancellationToken>(4)));

        var seen = new List<string>();
        var run = async () =>
        {
            await foreach (var line in Follow().StreamAsync(_id, Ns, PodName, Request, CancellationToken.None))
            {
                seen.Add(line);
            }
        };

        await run.Should().ThrowAsync<HttpOperationException>();
        seen.Should().Equal("x");
    }

    [Fact]
    public async Task Follow_passes_cancellation_to_the_stream()
    {
        CancellationToken observed = default;
        _ops.FollowLogsAsync(Secret, Ns, PodName, Request, Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                observed = ci.ArgAt<CancellationToken>(4);
                return Lines(["x", "y"], ct: observed);
            });
        using var cts = new CancellationTokenSource();

        var run = async () =>
        {
            await foreach (var _ in Follow().StreamAsync(_id, Ns, PodName, Request, cts.Token))
            {
                cts.Cancel();
            }
        };

        await run.Should().ThrowAsync<OperationCanceledException>();
        observed.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task Follow_does_not_log_an_error_when_the_caller_cancelled()
    {
        using var cts = new CancellationTokenSource();
        _ops.FollowLogsAsync(Secret, Ns, PodName, Request, Arg.Any<CancellationToken>())
            .Returns(_ => AbortedAfterCancel(cts));
        var logger = new ListLogger<FollowPodLogsQueryHandler>();
        var handler = new FollowPodLogsQueryHandler(_ops, _connections, logger);

        var run = async () =>
        {
            await foreach (var _ in handler.StreamAsync(_id, Ns, PodName, Request, cts.Token))
            {
            }
        };

        await run.Should().ThrowAsync<IOException>();
        logger.Levels.Should().NotContain(LogLevel.Error);

        // Cancelling the caller tears the HTTP stream down, which surfaces as an IOException rather than a cancellation.
        static async IAsyncEnumerable<string> AbortedAfterCancel(CancellationTokenSource cts)
        {
            yield return "x";
            await Task.Yield();
            await cts.CancelAsync();
            throw new IOException("The response ended prematurely.");
        }
    }

    [Fact]
    public async Task Follow_logs_an_error_when_the_stream_fails_on_its_own()
    {
        _ops.FollowLogsAsync(Secret, Ns, PodName, Request, Arg.Any<CancellationToken>())
            .Returns(ci => Lines(["x"], new IOException("reset"), ci.ArgAt<CancellationToken>(4)));
        var logger = new ListLogger<FollowPodLogsQueryHandler>();

        var run = async () =>
        {
            await foreach (var _ in new FollowPodLogsQueryHandler(_ops, _connections, logger).StreamAsync(_id, Ns, PodName, Request, CancellationToken.None))
            {
            }
        };

        await run.Should().ThrowAsync<IOException>();
        logger.Levels.Should().Contain(LogLevel.Error);
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Levels.Add(logLevel);
    }

    [Fact]
    public async Task Download_returns_the_bytes()
    {
        _ops.DownloadAllLogsAsync(Secret, Ns, PodName, Arg.Any<CancellationToken>()).Returns("==> gateway <==\n"u8.ToArray());

        var result = await Download().HandleAsync(_id, Ns, PodName);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Equal("==> gateway <==\n"u8.ToArray());
    }

    [Fact]
    public async Task Download_failure_is_friendly()
    {
        _ops.DownloadAllLogsAsync(Secret, Ns, PodName, Arg.Any<CancellationToken>()).ThrowsAsync(Http(HttpStatusCode.NotFound));

        var result = await Download().HandleAsync(_id, Ns, PodName);

        result.Error.Should().Be("It no longer exists — refresh.");
    }
}
