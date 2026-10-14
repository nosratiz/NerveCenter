using System.Text;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using NSubstitute;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Plugins.Kubernetes.Components;
using SbConsole.Sdk;
using PodPage = SbConsole.Plugins.Kubernetes.Pages.PodDetail;

namespace SbConsole.Plugins.Kubernetes.Tests.Pages;

public class PodDetailPageTests : K8sPageTestBase
{
    private const string Ns = "checkout";
    private const string PodName = "payment-gateway-7d9f8-x2k4q";

    public PodDetailPageTests()
    {
        Operations.GetPodDetailAsync(Secret, Ns, PodName, Arg.Any<CancellationToken>())
            .Returns(_ => TestData.CrashLoopPodDetail());
        Operations.ReadLogsAsync(Secret, Ns, PodName, Arg.Any<K8sLogRequest>(), Arg.Any<CancellationToken>())
            .Returns(["info gateway starting", "error runtime: out of memory"]);
    }

    private int DetailCalls() =>
        Operations.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IKubernetesOperations.GetPodDetailAsync));

    private IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderPage(
        string name = PodName, string? query = null, string ready = ".k8s-facts, .k8s-pod-gone, .k8s-pod-error, .k8s-pod-noconn")
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo(
            $"{KubernetesPlugin.PodHref(Ns, name)}?{query ?? "connectionId=" + ConnectionId}");

        RenderFragment fragment = builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudDialogProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<PodPage>(2);
            builder.AddAttribute(3, nameof(PodPage.Namespace), Ns);
            builder.AddAttribute(4, nameof(PodPage.Name), name);
            builder.CloseComponent();
        };

        var cut = Render(fragment);
        cut.WaitForState(() => cut.FindAll(ready).Count > 0, TimeSpan.FromSeconds(5));
        return cut;
    }

    private static string Text(AngleSharp.Dom.IElement e) =>
        string.Join(" ", e.TextContent.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries));

    private static void OpenTab(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string label) =>
        cut.FindAll(".mud-tab").First(t => t.TextContent.Trim().StartsWith(label, StringComparison.Ordinal)).Click();

    private IEnumerable<K8sLogRequest> ReadRequests() =>
        Operations.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IKubernetesOperations.ReadLogsAsync))
            .Select(c => (K8sLogRequest)c.GetArguments()[3]!);

    [Fact]
    public void Header_has_breadcrumbs_to_workloads_namespace_and_the_owning_deployment()
    {
        var cut = RenderPage();

        var crumbs = cut.Find(".k8s-breadcrumbs");
        Text(crumbs).Should().Contain(ConnectionName).And.Contain("checkout").And.Contain("payment-gateway").And.Contain(PodName);
        var links = crumbs.QuerySelectorAll("a").Select(a => a.GetAttribute("href")).ToList();
        links.Should().Contain($"{KubernetesPlugin.WorkloadsHref}?connectionId={ConnectionId}");
        links.Should().Contain($"{KubernetesPlugin.WorkloadsHref}?connectionId={ConnectionId}&ns=checkout");
        links.Should().Contain($"{KubernetesPlugin.DeploymentHref(Ns, "payment-gateway")}?connectionId={ConnectionId}");
        cut.Find(".k8s-kind-chip").TextContent.Trim().Should().Be("Pod");
        cut.FindAll(".k8s-prod-chip").Should().HaveCount(1);
        cut.FindAll(".k8s-pod-yaml").Should().HaveCount(1);
    }

    [Fact]
    public void Facts_match_mockup_3d()
    {
        var cut = RenderPage();

        var facts = Text(cut.Find(".k8s-facts"));
        facts.Should().Contain("CrashLoopBackOff").And.Contain("↻ 14").And.Contain("Running / not ready")
            .And.Contain("aks-pool1-38211-vmss000004").And.Contain("Ready · memory 91%")
            .And.Contain("10.244.3.118").And.Contain("host 10.240.0.7")
            .And.Contain("Burstable").And.Contain("req 500m / 512 Mi · lim — / 512 Mi")
            .And.Contain("ReplicaSet payment-gateway-7d9f8").And.Contain("rev 23 · started 38m ago")
            .And.Contain("payment-gateway").And.Contain("2 volumes · 1 secret mount");
    }

    [Fact]
    public void Containers_table_lists_init_and_app_containers_with_the_termination_chip()
    {
        var cut = RenderPage();

        var rows = cut.FindAll(".k8s-containers tbody tr");
        rows.Should().HaveCount(3);
        Text(rows[0]).Should().Contain("init · migrate").And.Contain("Terminated · Completed").And.Contain("exit 0");
        Text(rows[1]).Should().Contain("gateway").And.Contain("ghcr.io/nervecenter/payment-gateway:2.14.1")
            .And.Contain("Waiting · CrashLoopBackOff").And.Contain("2m ago · ran 41s").And.Contain("14")
            .And.Contain("no").And.Contain("8080/http · 9090/metrics");
        Text(rows[2]).Should().Contain("otel-sidecar").And.Contain("Running").And.Contain("yes").And.Contain("4317/grpc");

        var chip = cut.Find(".k8s-term-chip");
        Text(chip).Should().Be("exit 137 · OOMKilled");
        chip.ClassList.Should().Contain("mud-chip-color-error");
        rows[0].QuerySelectorAll(".k8s-term-chip").Should().BeEmpty();
        rows[2].TextContent.Should().Contain("—");
    }

    [Fact]
    public void Logs_tab_defaults_to_the_crashed_gateway_instance()
    {
        var cut = RenderPage();

        cut.WaitForAssertion(() => ReadRequests().Should().ContainSingle().Which.Should().Be(new K8sLogRequest("gateway", true, 500, true)));
        cut.WaitForAssertion(() => cut.Find(".k8s-log-exit").TextContent.Should().Contain("Killed by the kernel at the memory limit (512 Mi)."));
    }

    [Fact]
    public void Query_string_picks_the_log_container_and_instance()
    {
        var cut = RenderPage(query: $"connectionId={ConnectionId}&container=otel-sidecar&previous=false");

        cut.WaitForAssertion(() => ReadRequests().Should().ContainSingle().Which.Should().Be(new K8sLogRequest("otel-sidecar", false, 500, true)));
    }

    [Fact]
    public void Events_tab_lists_the_pod_events()
    {
        var cut = RenderPage();

        OpenTab(cut, "Events");

        cut.WaitForAssertion(() => Text(cut.Find(".k8s-pod-events")).Should().Contain("BackOff").And.Contain("Back-off restarting failed container gateway"));
        cut.FindAll(".mud-tab").Select(t => Text(t)).Should().Contain("Events 1");
    }

    [Fact]
    public void Describe_tab_shows_the_read_only_yaml()
    {
        var cut = RenderPage();

        OpenTab(cut, "Describe");

        cut.WaitForAssertion(() => cut.Find(".k8s-pod-describe pre.k8s-yaml").TextContent.Should().Contain($"name: {PodName}"));
        cut.Find(".k8s-pod-describe").TextContent.Should().Contain("read-only · managedFields hidden");
    }

    [Fact]
    public void Env_tab_masks_secret_rows_and_never_shows_a_secret_value()
    {
        var cut = RenderPage();

        OpenTab(cut, "Env");

        cut.WaitForAssertion(() => cut.FindAll(".k8s-env tbody tr").Should().HaveCount(8));
        var stripe = cut.FindAll(".k8s-env tbody tr").Single(r => r.TextContent.Contains("STRIPE_SECRET_KEY"));
        Text(stripe).Should().Contain("••••••••").And.Contain("secret stripe-live");
        var db = cut.FindAll(".k8s-env tbody tr").Single(r => r.TextContent.Contains("DB_PASSWORD"));
        Text(db).Should().Contain("••••••••").And.Contain("secret pg-app-rw");
        Text(cut.FindAll(".k8s-env tbody tr").Single(r => r.TextContent.Contains("REDIS_URL")))
            .Should().Contain("redis://cart-redis:6379/0").And.Contain("cm gateway-config");
        Text(cut.FindAll(".k8s-env tbody tr").Single(r => r.TextContent.Contains("POD_NAME")))
            .Should().Contain("fieldRef metadata.name");
        cut.Find(".k8s-env-caption").TextContent.Should().Contain("values from Secrets are never shown");
        cut.FindAll(".mud-tab").Select(t => Text(t)).Should().Contain("Env 9");
    }

    [Fact]
    public void Env_tab_never_renders_a_secret_value_even_if_one_slipped_into_the_model()
    {
        // Defence in depth: the operations layer never fills Value for a Secret row, but the page must not print it either.
        var detail = TestData.CrashLoopPodDetail();
        Operations.GetPodDetailAsync(Secret, Ns, PodName, Arg.Any<CancellationToken>())
            .Returns(detail with { Env = [new K8sEnvVar("gateway", "STRIPE_SECRET_KEY", "sk_live_leaked", K8sEnvSource.Secret, "stripe-live")] });
        var cut = RenderPage();

        OpenTab(cut, "Env");

        cut.WaitForAssertion(() => cut.FindAll(".k8s-env tbody tr").Should().HaveCount(1));
        cut.Markup.Should().NotContain("sk_live_leaked");
    }

    [Fact]
    public void Env_tab_shows_twenty_rows_then_show_all()
    {
        var detail = TestData.CrashLoopPodDetail();
        var many = Enumerable.Range(0, 25).Select(i => new K8sEnvVar("gateway", $"VAR_{i:00}", $"v{i}", K8sEnvSource.Literal, null)).ToList();
        Operations.GetPodDetailAsync(Secret, Ns, PodName, Arg.Any<CancellationToken>()).Returns(detail with { Env = many });
        var cut = RenderPage();
        OpenTab(cut, "Env");

        cut.WaitForAssertion(() => cut.FindAll(".k8s-env tbody tr.k8s-env-row").Should().HaveCount(20));
        Text(cut.Find(".k8s-env-all")).Should().Contain("5 more");
        cut.Find(".k8s-env-all button").Click();

        cut.WaitForAssertion(() => cut.FindAll(".k8s-env tbody tr.k8s-env-row").Should().HaveCount(25));
    }

    [Fact]
    public void Delete_pod_sends_a_destructive_prod_request_and_navigates_to_the_deployment()
    {
        ConfirmationRequest? asked = null;
        Confirmation.ConfirmAsync(Arg.Do<ConfirmationRequest>(r => asked = r), Arg.Any<CancellationToken>()).Returns(true);
        var cut = RenderPage();

        cut.Find(".k8s-pod-menu button").Click();
        cut.WaitForAssertion(() => cut.FindAll(".k8s-delete-pod").Should().HaveCount(1));
        cut.Find(".k8s-delete-pod").Click();

        cut.WaitForAssertion(() => Operations.Received(1).DeletePodAsync(Secret, Ns, PodName, null, Arg.Any<CancellationToken>()));
        asked!.Verb.Should().Be("Delete pod");
        asked.Target.Should().Be(PodName);
        asked.IsProd.Should().BeTrue();
        asked.Destructive.Should().BeTrue();
        asked.AuditAction.Should().Be("k8s.pod.delete");
        asked.Consequence.Should().Be("Its Deployment will create a replacement.");
        asked.Details.Should().BeEquivalentTo(new[]
        {
            new ConfirmationDetail("Node", "aks-pool1-38211-vmss000004"),
            new ConfirmationDetail("Status", "CrashLoopBackOff"),
        });
        var nav = Services.GetRequiredService<NavigationManager>();
        cut.WaitForAssertion(() => nav.Uri.Should().EndWith($"{KubernetesPlugin.DeploymentHref(Ns, "payment-gateway")}?connectionId={ConnectionId}"));
    }

    [Fact]
    public void Deleting_a_bare_pod_says_it_will_not_come_back_and_returns_to_workloads()
    {
        ConfirmationRequest? asked = null;
        Confirmation.ConfirmAsync(Arg.Do<ConfirmationRequest>(r => asked = r), Arg.Any<CancellationToken>()).Returns(true);
        var detail = TestData.CrashLoopPodDetail();
        Operations.GetPodDetailAsync(Secret, Ns, PodName, Arg.Any<CancellationToken>())
            .Returns(detail with { Pod = detail.Pod with { Owner = null }, OwnerChain = [] });
        var cut = RenderPage();

        cut.Find(".k8s-pod-menu button").Click();
        cut.WaitForAssertion(() => cut.FindAll(".k8s-delete-pod").Should().HaveCount(1));
        cut.Find(".k8s-delete-pod").Click();

        cut.WaitForAssertion(() => Operations.Received(1).DeletePodAsync(Secret, Ns, PodName, null, Arg.Any<CancellationToken>()));
        asked!.Consequence.Should().Be("Nothing owns this pod; it won't come back.");
        var nav = Services.GetRequiredService<NavigationManager>();
        cut.WaitForAssertion(() => nav.Uri.Should().EndWith($"{KubernetesPlugin.WorkloadsHref}?connectionId={ConnectionId}&ns=checkout"));
    }

    [Fact]
    public async Task Declined_delete_does_nothing()
    {
        Confirmation.ConfirmAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>()).Returns(false);
        var cut = RenderPage();

        cut.Find(".k8s-pod-menu button").Click();
        cut.WaitForAssertion(() => cut.FindAll(".k8s-delete-pod").Should().HaveCount(1));
        cut.Find(".k8s-delete-pod").Click();

        await Task.Delay(50);
        await Operations.DidNotReceiveWithAnyArgs().DeletePodAsync(default!, default!, default!, default, default);
    }

    [Fact]
    public void Viewer_menu_has_copy_and_download_but_no_delete()
    {
        Operations.GetAccessAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => TestData.ViewerAccess());
        var cut = RenderPage();

        cut.Find(".k8s-pod-menu button").Click();

        cut.WaitForAssertion(() => cut.FindAll(".k8s-download-logs").Should().HaveCount(1));
        cut.FindAll(".k8s-copy-pod").Should().HaveCount(1);
        cut.FindAll(".k8s-delete-pod").Should().BeEmpty();
    }

    [Fact]
    public void Download_logs_saves_the_handler_bytes_as_pod_log()
    {
        var module = JSInterop.SetupModule(K8sJs.ModulePath);
        module.Mode = JSRuntimeMode.Loose;
        Operations.DownloadAllLogsAsync(Secret, Ns, PodName, Arg.Any<CancellationToken>())
            .Returns(Encoding.UTF8.GetBytes("==> gateway <==\nboom\n"));
        var cut = RenderPage();

        cut.Find(".k8s-pod-menu button").Click();
        cut.WaitForAssertion(() => cut.FindAll(".k8s-download-logs").Should().HaveCount(1));
        cut.Find(".k8s-download-logs").Click();

        cut.WaitForAssertion(() => module.VerifyInvoke("downloadFile").Arguments[0].Should().Be($"{PodName}.log"));
    }

    [Fact]
    public void Copy_pod_name_puts_the_name_on_the_clipboard()
    {
        var module = JSInterop.SetupModule(K8sJs.ModulePath);
        module.Mode = JSRuntimeMode.Loose;
        var cut = RenderPage();

        cut.Find(".k8s-pod-menu button").Click();
        cut.WaitForAssertion(() => cut.FindAll(".k8s-copy-pod").Should().HaveCount(1));
        cut.Find(".k8s-copy-pod").Click();

        cut.WaitForAssertion(() => module.VerifyInvoke("copyText").Arguments[0].Should().Be(PodName));
    }

    [Fact]
    public void Yaml_opens_the_yaml_dialog()
    {
        Operations.GetYamlAsync(Secret, K8sResourceKind.Pod, Ns, PodName, Arg.Any<CancellationToken>()).Returns("kind: Pod\n");
        var cut = RenderPage();

        cut.Find(".k8s-pod-yaml").Click();

        cut.WaitForAssertion(() => cut.FindAll("pre.k8s-yaml").Should().Contain(p => p.TextContent.Contains("kind: Pod")));
    }

    [Fact]
    public void Missing_pod_says_it_no_longer_exists()
    {
        Operations.GetPodDetailAsync(Secret, Ns, "x", Arg.Any<CancellationToken>()).Returns((K8sPodDetail?)null);
        var cut = RenderPage("x");

        cut.Find(".k8s-pod-gone").TextContent.Should().Contain("Pod checkout/x no longer exists.");
        cut.FindAll(".k8s-facts").Should().BeEmpty();
    }

    [Fact]
    public void Missing_connection_id_shows_a_link_back_to_workloads()
    {
        var cut = RenderPage(query: "");

        cut.Find(".k8s-pod-noconn").QuerySelector("a")!.GetAttribute("href").Should().Be(KubernetesPlugin.WorkloadsHref);
        Operations.DidNotReceiveWithAnyArgs().GetPodDetailAsync(default!, default!, default!, default);
    }

    [Fact]
    public void A_failed_load_shows_the_error_with_retry()
    {
        Operations.GetPodDetailAsync(Secret, Ns, PodName, Arg.Any<CancellationToken>())
            .Returns<K8sPodDetail?>(_ => throw new HttpRequestException("boom"));
        var cut = RenderPage();

        cut.FindAll(".k8s-pod-error").Should().HaveCount(1);
        Operations.GetPodDetailAsync(Secret, Ns, PodName, Arg.Any<CancellationToken>()).Returns(_ => TestData.CrashLoopPodDetail());
        cut.Find(".k8s-pod-retry").Click();

        cut.WaitForAssertion(() => cut.FindAll(".k8s-facts").Should().HaveCount(1));
    }

    [Fact]
    public void Poller_reloads_the_detail_every_ten_seconds_but_not_the_logs()
    {
        var cut = RenderPage();
        cut.WaitForAssertion(() => ReadRequests().Should().HaveCount(1));
        var loads = DetailCalls();

        Time.Advance(TimeSpan.FromSeconds(10));

        cut.WaitForAssertion(() => DetailCalls().Should().Be(loads + 1));
        ReadRequests().Should().HaveCount(1);
    }
}
