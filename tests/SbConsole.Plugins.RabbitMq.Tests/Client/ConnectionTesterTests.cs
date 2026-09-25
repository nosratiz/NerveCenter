using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using RabbitMQ.Client.Exceptions;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.RabbitMq.Tests.Client;

public class ConnectionTesterTests
{
    private const string PlainSecret =
        "host=localhost;amqpPort=5673;managementUrl=http%3A%2F%2Flocalhost%3A15672;vhost=%2Forders;username=u;password=p";

    private const string AmqpUnreachable = "AMQP endpoint unreachable — check host, port and TLS";
    private const string ManagementRejected = "Management API rejected the credentials";

    private static readonly ManagementProbe Probe = new("3.13.7", "rabbit@node1", 12, 9, ["management", "monitoring"]);

    private readonly FakeTimeProvider _clock = new();

    private Func<RabbitConnectionSettings, CancellationToken, Task> AmqpOk(int ms = 0) => (_, _) =>
    {
        _clock.Advance(TimeSpan.FromMilliseconds(ms));
        return Task.CompletedTask;
    };

    private Func<RabbitConnectionSettings, CancellationToken, Task<ManagementProbe>> ManagementOk(int ms = 0) => (_, _) =>
    {
        _clock.Advance(TimeSpan.FromMilliseconds(ms));
        return Task.FromResult(Probe);
    };

    private static Task AmqpFails(RabbitConnectionSettings s, CancellationToken ct) =>
        Task.FromException(new BrokerUnreachableException(new IOException("connection refused")));

    private static Task<ManagementProbe> ManagementFails(RabbitConnectionSettings s, CancellationToken ct) =>
        Task.FromException<ManagementProbe>(new ManagementApiException(401, "GET", "/api/overview", "Unauthorized"));

    private ConnectionTester Tester(
        Func<RabbitConnectionSettings, CancellationToken, Task> amqp,
        Func<RabbitConnectionSettings, CancellationToken, Task<ManagementProbe>> management) =>
        new(amqp, management, _clock);

    [Fact]
    public async Task Both_probes_pass()
    {
        var result = await Tester(AmqpOk(41), ManagementOk(7)).TestAsync(PlainSecret);

        result.Success.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        result.Identity.Should().Be("RabbitMQ 3.13.7 · vhost /orders · 12 exchanges · 9 queues · tags management, monitoring");
        result.Checks.Should().Equal(
            new ConnectionCheck("AMQP 5673 · plain", ConnectionCheckStatus.Passed, "41 ms"),
            new ConnectionCheck("Management API 15672", ConnectionCheckStatus.Passed, "7 ms"));
    }

    [Fact]
    public async Task Amqp_only_is_a_success_with_a_failed_management_check()
    {
        var result = await Tester(AmqpOk(5), ManagementFails).TestAsync(PlainSecret);

        result.Success.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        result.Identity.Should().Be("vhost /orders");
        result.Checks.Should().Equal(
            new ConnectionCheck("AMQP 5673 · plain", ConnectionCheckStatus.Passed, "5 ms"),
            new ConnectionCheck("Management API 15672", ConnectionCheckStatus.Failed,
                ManagementRejected + " — messages will send and receive, but queue depths, rates and bindings stay blank until the management plugin is reachable."));
    }

    [Fact]
    public async Task Management_only_is_a_success_with_a_failed_amqp_check()
    {
        var result = await Tester(AmqpFails, ManagementOk(3)).TestAsync(PlainSecret);

        result.Success.Should().BeTrue();
        result.Identity.Should().Be("RabbitMQ 3.13.7 · vhost /orders · 12 exchanges · 9 queues · tags management, monitoring");
        result.Checks.Should().Equal(
            new ConnectionCheck("AMQP 5673 · plain", ConnectionCheckStatus.Failed,
                AmqpUnreachable + " — the console can read the broker but cannot publish or get messages."),
            new ConnectionCheck("Management API 15672", ConnectionCheckStatus.Passed, "3 ms"));
    }

    [Fact]
    public async Task Both_failing_is_a_failure_carrying_the_amqp_error_and_both_checks()
    {
        var result = await Tester(AmqpFails, ManagementFails).TestAsync(PlainSecret);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be(AmqpUnreachable);
        result.Identity.Should().BeNull();
        result.Checks.Should().Equal(
            new ConnectionCheck("AMQP 5673 · plain", ConnectionCheckStatus.Failed, AmqpUnreachable),
            new ConnectionCheck("Management API 15672", ConnectionCheckStatus.Failed, ManagementRejected));
    }

    [Fact]
    public async Task A_probe_that_hangs_times_out_after_ten_seconds_without_blocking_the_other()
    {
        var hung = new TaskCompletionSource();
        CancellationToken seen = default;
        var tester = Tester(
            (_, ct) =>
            {
                seen = ct;
                return hung.Task; // ignores its token entirely: the tester must still give up
            },
            ManagementOk(2));

        var pending = tester.TestAsync(PlainSecret);
        pending.IsCompleted.Should().BeFalse();

        _clock.Advance(TimeSpan.FromSeconds(10));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));

        seen.IsCancellationRequested.Should().BeTrue("the probe's own token is cancelled at the timeout");
        result.Success.Should().BeTrue();
        result.Checks![0].Status.Should().Be(ConnectionCheckStatus.Failed);
        result.Checks[0].Detail.Should().StartWith("Timed out talking to the broker — ");
        result.Checks[1].Should().Be(new ConnectionCheck("Management API 15672", ConnectionCheckStatus.Passed, "2 ms"));
    }

    [Fact]
    public async Task Probes_run_concurrently()
    {
        var amqpStarted = new TaskCompletionSource();
        var managementStarted = new TaskCompletionSource();
        var tester = Tester(
            async (_, _) =>
            {
                amqpStarted.SetResult();
                await managementStarted.Task;
            },
            async (_, _) =>
            {
                managementStarted.SetResult();
                await amqpStarted.Task;
                return Probe;
            });

        var result = await tester.TestAsync(PlainSecret).WaitAsync(TimeSpan.FromSeconds(5));

        result.Checks.Should().OnlyContain(c => c.Status == ConnectionCheckStatus.Passed);
    }

    [Theory]
    [InlineData("host=rabbit;username=u;password=p;tls=true", "AMQP 5671 · TLS verified", "Management API 15671")]
    [InlineData("host=rabbit;username=u;password=p;tls=true;verifyCert=false", "AMQP 5671 · TLS (unverified)", "Management API 15671")]
    [InlineData("host=rabbit;username=u;password=p", "AMQP 5672 · plain", "Management API 15672")]
    [InlineData("host=rabbit;username=u;password=p;managementUrl=https%3A%2F%2Fmgmt.example%2Frabbit%2F", "AMQP 5672 · plain", "Management API 443")]
    public async Task Labels_name_the_port_and_transport(string secret, string amqpLabel, string managementLabel)
    {
        var result = await Tester(AmqpOk(), ManagementOk()).TestAsync(secret);

        result.Checks!.Select(c => c.Label).Should().Equal(amqpLabel, managementLabel);
    }

    [Fact]
    public async Task Empty_user_tags_read_as_none()
    {
        var tester = Tester(AmqpOk(), (_, _) => Task.FromResult(Probe with { UserTags = [] }));

        var result = await tester.TestAsync("host=rabbit;username=u;password=p");

        result.Identity.Should().Be("RabbitMQ 3.13.7 · vhost / · 12 exchanges · 9 queues · tags none");
    }

    [Fact]
    public async Task An_unparseable_secret_fails_without_probing()
    {
        var probed = false;
        var tester = Tester(
            (_, _) => { probed = true; return Task.CompletedTask; },
            (_, _) => { probed = true; return Task.FromResult(Probe); });

        var result = await tester.TestAsync("username=u;password=p");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Connection is missing 'host'.");
        result.Checks.Should().BeNull();
        probed.Should().BeFalse();
    }

    [Fact]
    public async Task Caller_cancellation_propagates_instead_of_reporting_a_timeout()
    {
        using var cts = new CancellationTokenSource();
        var tester = Tester(
            (_, ct) => Task.Delay(Timeout.Infinite, ct),
            (_, ct) => Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => Probe, TaskScheduler.Default));

        var pending = tester.TestAsync(PlainSecret, cts.Token);
        await cts.CancelAsync();

        await pending.Invoking(t => t.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().ThrowAsync<OperationCanceledException>();
    }
}
