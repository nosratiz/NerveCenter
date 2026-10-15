using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using k8s;
using k8s.Autorest;
using k8s.Models;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Plugins.Kubernetes.Tests.Infrastructure;

namespace SbConsole.Plugins.Kubernetes.Tests.Operations;

[Collection("k3s")]
public class LogOperationsTests(K3sFixture k3s)
{
    private const string Ns = "sbc-logs";
    private const string Crash = "crash";
    private const string Ticker = "ticker";
    private const string Image = "busybox:1.36";

    // The two pods are shared by every test in the class (one k3s per run): seeded once, never deleted.
    private static readonly SemaphoreSlim SeedLock = new(1, 1);
    private static bool _seeded;

    private static V1Pod ShellPod(string name, string script) => new()
    {
        Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = Ns },
        Spec = new V1PodSpec
        {
            RestartPolicy = "Always",
            TerminationGracePeriodSeconds = 0,
            Containers = [new V1Container { Name = "busybox", Image = Image, Command = ["sh", "-c", script] }],
        },
    };

    private async Task SeedAsync()
    {
        await SeedLock.WaitAsync();
        try
        {
            if (_seeded)
            {
                return;
            }

            await k3s.EnsureNamespaceAsync(Ns);
            await IgnoreConflict(() => k3s.Admin.CoreV1.CreateNamespacedPodAsync(ShellPod(Crash, "echo one; echo two; exit 3"), Ns));
            await IgnoreConflict(() => k3s.Admin.CoreV1.CreateNamespacedPodAsync(
                ShellPod(Ticker, "i=0; while true; do echo tick $i; i=$((i+1)); sleep 1; done"), Ns));

            // The crash pod has restarted at least once (so a previous instance exists); the ticker is running.
            await K3sFixture.WaitAsync(async () =>
            {
                var pod = await k3s.Admin.CoreV1.ReadNamespacedPodAsync(Crash, Ns);
                return pod.Status?.ContainerStatuses?.FirstOrDefault()?.RestartCount >= 1;
            }, TimeSpan.FromMinutes(3));
            await K3sFixture.WaitAsync(async () =>
            {
                var pod = await k3s.Admin.CoreV1.ReadNamespacedPodAsync(Ticker, Ns);
                return pod.Status?.ContainerStatuses?.FirstOrDefault()?.State?.Running is not null;
            }, TimeSpan.FromMinutes(3));
            _seeded = true;
        }
        finally
        {
            SeedLock.Release();
        }
    }

    private static async Task IgnoreConflict(Func<Task> create)
    {
        try
        {
            await create();
        }
        catch (HttpOperationException ex) when ((int)ex.Response.StatusCode == 409)
        {
            // Already exists.
        }
    }

    /// <summary>
    /// Retries a read until it satisfies <paramref name="ok"/>: a crash-looping container's logs come and go with its restarts
    /// (while the kubelet swaps instances it answers 200 with a single "unable to retrieve container logs…" line).
    /// </summary>
    private static async Task<IReadOnlyList<string>> EventuallyAsync(Func<Task<IReadOnlyList<string>>> read, Func<IReadOnlyList<string>, bool> ok)
    {
        IReadOnlyList<string> lines = [];
        await K3sFixture.WaitAsync(async () =>
        {
            lines = await read();
            return ok(lines);
        }, TimeSpan.FromMinutes(1));
        return lines;
    }

    [DockerFact]
    public async Task Previous_logs_hold_the_crashed_instance()
    {
        await SeedAsync();

        var lines = await EventuallyAsync(
            () => k3s.Operations.ReadLogsAsync(k3s.AdminSecret, Ns, Crash, new K8sLogRequest("busybox", true, 500, false)),
            l => l.SequenceEqual(["one", "two"]));

        lines.Should().Equal("one", "two");
    }

    [DockerFact]
    public async Task Current_logs_are_read_as_lines()
    {
        await SeedAsync();

        var lines = await EventuallyAsync(
            () => k3s.Operations.ReadLogsAsync(k3s.AdminSecret, Ns, Ticker, new K8sLogRequest("busybox", false, 500, false)),
            l => l.Count >= 2);

        lines[0].Should().Be("tick 0");
        lines.Should().AllSatisfy(l => l.Should().StartWith("tick "));
    }

    [DockerFact]
    public async Task Tail_limits_the_lines()
    {
        await SeedAsync();

        var lines = await EventuallyAsync(
            () => k3s.Operations.ReadLogsAsync(k3s.AdminSecret, Ns, Crash, new K8sLogRequest("busybox", true, 1, false)),
            l => l.SequenceEqual(["two"]));

        lines.Should().Equal("two");
    }

    [DockerFact]
    public async Task Timestamps_prefix_every_line()
    {
        await SeedAsync();

        var lines = await EventuallyAsync(
            () => k3s.Operations.ReadLogsAsync(k3s.AdminSecret, Ns, Crash, new K8sLogRequest("busybox", true, 500, true)),
            l => l.Count == 2 && l[1].EndsWith(" two", StringComparison.Ordinal));

        lines.Should().HaveCount(2);
        lines.Should().AllSatisfy(l => Regex.IsMatch(l, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?Z (one|two)$").Should().BeTrue(l));
    }

    [DockerFact]
    public async Task Previous_of_a_never_restarted_container_is_empty()
    {
        await SeedAsync();

        var lines = await k3s.Operations.ReadLogsAsync(k3s.AdminSecret, Ns, Ticker, new K8sLogRequest("busybox", true, 500, false));

        lines.Should().BeEmpty();
    }

    [DockerFact]
    public async Task Follow_yields_new_lines_and_stops_on_cancellation()
    {
        await SeedAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var seen = new List<string>();

        var follow = async () =>
        {
            await foreach (var line in k3s.Operations.FollowLogsAsync(k3s.AdminSecret, Ns, Ticker, new K8sLogRequest("busybox", false, 1, false), cts.Token))
            {
                seen.Add(line);
                if (seen.Count == 3)
                {
                    cts.Cancel();
                }
            }
        };

        await follow.Should().ThrowAsync<OperationCanceledException>();
        seen.Should().HaveCount(3);
        seen.Should().AllSatisfy(l => l.Should().StartWith("tick "));
        var numbers = seen.Select(l => int.Parse(l["tick ".Length..], System.Globalization.CultureInfo.InvariantCulture)).ToList();
        numbers.Should().BeInAscendingOrder();
        numbers[2].Should().BeGreaterThan(numbers[0]);
    }

    [DockerFact]
    public async Task Download_has_a_header_and_the_logs_of_every_container()
    {
        await SeedAsync();

        var text = "";
        await K3sFixture.WaitAsync(async () =>
        {
            text = Encoding.UTF8.GetString(await k3s.Operations.DownloadAllLogsAsync(k3s.AdminSecret, Ns, Ticker));
            return text.Contains("tick 1", StringComparison.Ordinal);
        }, TimeSpan.FromMinutes(1));

        text.Should().StartWith("==> busybox <==\n").And.Contain("tick 0\n");
    }

    [DockerFact]
    public async Task Missing_pod_logs_fail_with_not_found()
    {
        await SeedAsync();

        var read = () => k3s.Operations.ReadLogsAsync(k3s.AdminSecret, Ns, "nope", new K8sLogRequest("busybox", false, 10, false));

        (await read.Should().ThrowAsync<HttpOperationException>()).Which.Response.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
    }
}
