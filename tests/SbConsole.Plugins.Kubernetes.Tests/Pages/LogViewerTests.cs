using System.Net;
using System.Runtime.CompilerServices;
using Bunit;
using FluentAssertions;
using k8s.Autorest;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Plugins.Kubernetes.Components;

namespace SbConsole.Plugins.Kubernetes.Tests.Pages;

public class LogViewerTests : K8sPageTestBase
{
    private const string Ns = "checkout";
    private const string PodName = "payment-gateway-7d9f8-x2k4q";

    private static readonly string[] OomLines =
    [
        "2026-10-14T11:57:19.002Z info  gateway starting",
        "2026-10-14T11:57:30.771Z warn  sdk cache warm-up: loading merchant configs into memory",
        "2026-10-14T11:57:47.330Z WARN  heap 462 MiB · Memory pressure",
        "2026-10-14T11:57:59.007Z error runtime: out of memory allocating 64 MiB block",
    ];

    public LogViewerTests()
    {
        Operations.ReadLogsAsync(Secret, Ns, PodName, Arg.Any<K8sLogRequest>(), Arg.Any<CancellationToken>())
            .Returns(OomLines);
    }

    private IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderViewer(
        string? initialContainer = null, bool? initialPrevious = null, IReadOnlyList<K8sContainer>? containers = null)
    {
        var list = (containers ?? TestData.CrashLoopPodDetail().Containers).Select(LogContainer.From).ToList();
        var cut = RenderWithPopovers(b =>
        {
            b.OpenComponent<LogViewer>(0);
            b.AddAttribute(1, nameof(LogViewer.ConnectionId), ConnectionId);
            b.AddAttribute(2, nameof(LogViewer.Namespace), Ns);
            b.AddAttribute(3, nameof(LogViewer.Pod), PodName);
            b.AddAttribute(4, nameof(LogViewer.Containers), (IReadOnlyList<LogContainer>)list);
            b.AddAttribute(5, nameof(LogViewer.InitialContainer), initialContainer);
            b.AddAttribute(6, nameof(LogViewer.InitialPrevious), initialPrevious);
            b.CloseComponent();
        });
        cut.WaitForState(() => cut.FindAll("pre.k8s-log, .k8s-log-failed").Count > 0, TimeSpan.FromSeconds(5));
        return cut;
    }

    private IEnumerable<K8sLogRequest> ReadRequests() =>
        Operations.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IKubernetesOperations.ReadLogsAsync))
            .Select(c => (K8sLogRequest)c.GetArguments()[3]!);

    private static bool IsSelected(AngleSharp.Dom.IElement toggle) =>
        toggle.ClassList.Contains("mud-toggle-item-selected");

    [Fact]
    public void Defaults_to_the_previous_instance_of_the_crash_looping_container()
    {
        var cut = RenderViewer();

        ReadRequests().Should().ContainSingle().Which.Should().Be(new K8sLogRequest("gateway", true, 500, true));
        IsSelected(cut.Find(".k8s-log-previous")).Should().BeTrue();
        IsSelected(cut.Find(".k8s-log-current")).Should().BeFalse();
        cut.FindAll("pre.k8s-log .k8s-log-line").Should().HaveCount(4);
    }

    [Fact]
    public void Initial_container_and_instance_win_over_the_defaults()
    {
        RenderViewer("otel-sidecar", initialPrevious: false);

        ReadRequests().Should().ContainSingle().Which.Should().Be(new K8sLogRequest("otel-sidecar", false, 500, true));
    }

    [Fact]
    public void A_healthy_pod_defaults_to_the_current_instance_of_the_first_app_container()
    {
        var detail = TestData.CrashLoopPodDetail();
        RenderViewer(containers: detail.Containers.Where(c => c.Name != "gateway").ToList());

        ReadRequests().Should().ContainSingle().Which.Should().Be(new K8sLogRequest("otel-sidecar", false, 500, true));
    }

    [Fact]
    public void Previous_is_disabled_without_a_previous_instance()
    {
        var cut = RenderViewer("otel-sidecar");

        cut.Find(".k8s-log-previous").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Follow_is_disabled_on_previous()
    {
        var cut = RenderViewer();

        cut.Find(".k8s-log-follow input").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Switching_to_current_reloads_the_current_instance()
    {
        var cut = RenderViewer();

        cut.Find(".k8s-log-current").Click();

        cut.WaitForAssertion(() => ReadRequests().Last().Should().Be(new K8sLogRequest("gateway", false, 500, true)));
        cut.Find(".k8s-log-follow input").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public async Task Tail_change_reloads_with_the_new_tail()
    {
        var cut = RenderViewer();
        var tail = cut.FindComponents<MudSelect<int>>().Single(s => s.Instance.Class!.Contains("k8s-log-tail"));

        await cut.InvokeAsync(() => tail.Instance.ValueChanged.InvokeAsync(2000));

        cut.WaitForAssertion(() => ReadRequests().Last().Should().Be(new K8sLogRequest("gateway", true, 2000, true)));
    }

    [Fact]
    public async Task Timestamps_toggle_reloads_without_timestamps()
    {
        var cut = RenderViewer();

        await cut.InvokeAsync(() => cut.Find(".k8s-log-timestamps input").Change(false));

        cut.WaitForAssertion(() => ReadRequests().Last().Should().Be(new K8sLogRequest("gateway", true, 500, false)));
    }

    [Fact]
    public void Search_highlights_and_counts_case_insensitively()
    {
        var cut = RenderViewer();

        cut.Find(".k8s-log-search input").Input("memory");

        cut.WaitForAssertion(() => cut.FindAll("pre.k8s-log mark").Should().HaveCount(3));
        cut.FindAll("pre.k8s-log mark").Select(m => m.TextContent).Should().Equal("memory", "Memory", "memory");
        cut.Find(".k8s-log-matches").TextContent.Trim().Should().Be("1 / 3");
    }

    [Fact]
    public void Search_next_moves_the_current_match()
    {
        var cut = RenderViewer();
        cut.Find(".k8s-log-search input").Input("memory");

        cut.Find(".k8s-log-next").Click();

        cut.WaitForAssertion(() => cut.Find(".k8s-log-matches").TextContent.Trim().Should().Be("2 / 3"));
        cut.FindAll("pre.k8s-log mark.k8s-log-mark-current").Should().ContainSingle().Which.TextContent.Should().Be("Memory");
    }

    [Fact]
    public void Lines_are_coloured_by_their_level_token()
    {
        var cut = RenderViewer();

        var lines = cut.FindAll("pre.k8s-log .k8s-log-line");
        lines[0].ClassList.Should().Contain("k8s-log-info");
        lines[1].ClassList.Should().Contain("k8s-log-warn");
        lines[2].ClassList.Should().Contain("k8s-log-warn");
        lines[3].ClassList.Should().Contain("k8s-log-error");
    }

    [Fact]
    public void Level_tokens_must_be_whole_words()
    {
        Operations.ReadLogsAsync(Secret, Ns, PodName, Arg.Any<K8sLogRequest>(), Arg.Any<CancellationToken>())
            .Returns(["errors=0 information warned", "plain"]);
        var cut = RenderViewer();

        var lines = cut.FindAll("pre.k8s-log .k8s-log-line");
        lines[0].ClassList.Should().NotContain(["k8s-log-error", "k8s-log-warn", "k8s-log-info"]);
    }

    [Fact]
    public void Log_content_is_text_never_markup()
    {
        Operations.ReadLogsAsync(Secret, Ns, PodName, Arg.Any<K8sLogRequest>(), Arg.Any<CancellationToken>())
            .Returns(["<b>bold</b><script>alert(1)</script> <img src=x>"]);
        var cut = RenderViewer();
        cut.Find(".k8s-log-search input").Input("bold");

        cut.WaitForAssertion(() => cut.FindAll("pre.k8s-log mark").Should().HaveCount(1));
        var pre = cut.Find("pre.k8s-log");
        pre.QuerySelectorAll("b, script, img").Should().BeEmpty();
        pre.TextContent.Should().Contain("<b>bold</b><script>alert(1)</script>");
    }

    [Fact]
    public void Previous_load_ends_with_the_exit_code_interpretation()
    {
        var cut = RenderViewer();

        cut.Find(".k8s-log-exit").TextContent.Should().Contain("Killed by the kernel at the memory limit (512 Mi). Ran 41s.");
    }

    [Fact]
    public void Current_of_a_running_container_has_no_exit_footer()
    {
        var cut = RenderViewer("otel-sidecar");

        cut.FindAll(".k8s-log-exit").Should().BeEmpty();
    }

    [Fact]
    public void Current_of_a_terminated_container_explains_its_exit()
    {
        var cut = RenderViewer("migrate");

        ReadRequests().Last().Should().Be(new K8sLogRequest("migrate", false, 500, true));
        cut.Find(".k8s-log-exit").TextContent.Should().Contain("Completed (exit 0).");
    }

    [Fact]
    public void An_empty_previous_says_so()
    {
        Operations.ReadLogsAsync(Secret, Ns, PodName, Arg.Any<K8sLogRequest>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<string>());
        var cut = RenderViewer();

        cut.Find(".k8s-log-empty").TextContent.Should().Contain("No log lines");
    }

    [Fact]
    public void A_failed_load_is_shown_inline()
    {
        Operations.ReadLogsAsync(Secret, Ns, PodName, Arg.Any<K8sLogRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpOperationException("raw") { Response = new HttpResponseMessageWrapper(new HttpResponseMessage(HttpStatusCode.Forbidden), "") });
        var cut = RenderViewer();

        cut.Find(".k8s-log-failed").TextContent.Should().Contain("Your account isn't allowed to do that.");
    }

    private sealed class FollowStream
    {
        public readonly TaskCompletionSource<bool> Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? ThenThrow { get; init; }
        public CancellationToken Token { get; private set; }

        public async IAsyncEnumerable<string> Lines([EnumeratorCancellation] CancellationToken ct)
        {
            Token = ct;
            yield return "tick 0";
            yield return "error tick 1";
            await Task.Yield();
            if (ThenThrow is not null)
            {
                throw ThenThrow;
            }

            try
            {
                await Release.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult(true);
                throw;
            }

            yield return "tick 2";
        }
    }

    private FollowStream SetupFollow(Exception? thenThrow = null)
    {
        var stream = new FollowStream { ThenThrow = thenThrow };
        Operations.FollowLogsAsync(Secret, Ns, PodName, Arg.Any<K8sLogRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => stream.Lines(ci.ArgAt<CancellationToken>(4)));
        return stream;
    }

    private IRenderedComponent<Bunit.Rendering.ContainerFragment> StartFollowing()
    {
        var cut = RenderViewer("otel-sidecar");
        cut.Find(".k8s-log-follow input").Change(true);
        return cut;
    }

    [Fact]
    public void Follow_streams_lines_with_a_throttled_repaint()
    {
        var stream = SetupFollow();
        var cut = StartFollowing();

        Operations.Received(1).FollowLogsAsync(Secret, Ns, PodName, new K8sLogRequest("otel-sidecar", false, 500, true), Arg.Any<CancellationToken>());
        Time.Advance(TimeSpan.FromMilliseconds(250));
        cut.WaitForAssertion(() => cut.FindAll("pre.k8s-log .k8s-log-line").Select(l => l.TextContent).Should().Equal("tick 0", "error tick 1"));
        cut.FindAll("pre.k8s-log .k8s-log-line")[1].ClassList.Should().Contain("k8s-log-error");

        stream.Release.SetResult();
        Time.Advance(TimeSpan.FromMilliseconds(250));
        cut.WaitForAssertion(() => cut.FindAll("pre.k8s-log .k8s-log-line").Should().HaveCount(3));
    }

    [Fact]
    public void Follow_keeps_only_the_tail()
    {
        Operations.FollowLogsAsync(Secret, Ns, PodName, Arg.Any<K8sLogRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Many(150));
        var cut = RenderViewer("otel-sidecar");
        var tail = cut.FindComponents<MudSelect<int>>().Single(s => s.Instance.Class!.Contains("k8s-log-tail"));
        cut.InvokeAsync(() => tail.Instance.ValueChanged.InvokeAsync(100));

        cut.Find(".k8s-log-follow input").Change(true);
        Time.Advance(TimeSpan.FromMilliseconds(250));

        cut.WaitForAssertion(() => cut.FindAll("pre.k8s-log .k8s-log-line").Should().HaveCount(100));
        cut.FindAll("pre.k8s-log .k8s-log-line")[0].TextContent.Should().Be("line 50");

        static async IAsyncEnumerable<string> Many(int n)
        {
            for (var i = 0; i < n; i++)
            {
                await Task.Yield();
                yield return $"line {i}";
            }
        }
    }

    [Fact]
    public async Task Turning_follow_off_cancels_the_stream()
    {
        var stream = SetupFollow();
        var cut = StartFollowing();

        cut.Find(".k8s-log-follow input").Change(false);

        (await stream.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
    }

    /// <summary>Hosts the viewer the way a page does, so a re-render without it removes (and disposes) it.</summary>
    private sealed class Host : ComponentBase
    {
        [Parameter] public bool Show { get; set; } = true;

        [Parameter] public RenderFragment? Viewer { get; set; }

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            if (Show)
            {
                builder.AddContent(1, Viewer);
            }
        }
    }

    [Fact]
    public async Task Removing_the_viewer_cancels_the_stream()
    {
        var stream = SetupFollow();
        var containers = TestData.CrashLoopPodDetail().Containers.Select(LogContainer.From).ToList();
        RenderFragment viewer = b =>
        {
            b.OpenComponent<LogViewer>(0);
            b.AddAttribute(1, nameof(LogViewer.ConnectionId), ConnectionId);
            b.AddAttribute(2, nameof(LogViewer.Namespace), Ns);
            b.AddAttribute(3, nameof(LogViewer.Pod), PodName);
            b.AddAttribute(4, nameof(LogViewer.Containers), (IReadOnlyList<LogContainer>)containers);
            b.AddAttribute(5, nameof(LogViewer.InitialContainer), "otel-sidecar");
            b.CloseComponent();
        };
        var host = Render<Host>(p => p.Add(h => h.Viewer, viewer));
        host.Find(".k8s-log-follow input").Change(true);
        stream.Token.CanBeCanceled.Should().BeTrue();
        stream.Token.IsCancellationRequested.Should().BeFalse();

        host.Render(p => p.Add(h => h.Show, false));

        host.FindAll(".k8s-log-viewer").Should().BeEmpty();
        stream.Token.IsCancellationRequested.Should().BeTrue();
        (await stream.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
    }

    [Fact]
    public void A_stream_error_is_shown_inline()
    {
        SetupFollow(new HttpOperationException("raw") { Response = new HttpResponseMessageWrapper(new HttpResponseMessage(HttpStatusCode.Unauthorized), "") });
        var cut = StartFollowing();

        cut.WaitForAssertion(() => cut.Find(".k8s-log-failed").TextContent.Should().Contain("The cluster rejected the credentials, or they have expired."));
        cut.FindAll("pre.k8s-log .k8s-log-line").Should().HaveCount(2);
    }

    [Fact]
    public void Follow_scrolls_to_the_bottom_after_a_repaint()
    {
        var module = JSInterop.SetupModule(K8sJs.ModulePath);
        module.Mode = JSRuntimeMode.Loose;
        SetupFollow();
        var cut = StartFollowing();
        int Scrolls() => module.Invocations.Count(i => i.Identifier == "scrollToBottom");
        var before = Scrolls();

        Time.Advance(TimeSpan.FromMilliseconds(250));

        cut.WaitForAssertion(() => Scrolls().Should().BeGreaterThan(before));
        module.Invocations.Where(i => i.Identifier == "scrollToBottom").Should().AllSatisfy(i => i.Arguments[0].Should().Be(cut.Find("pre.k8s-log").Id));
    }
}
