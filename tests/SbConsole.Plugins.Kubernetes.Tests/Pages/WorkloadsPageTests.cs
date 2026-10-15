using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using NSubstitute;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Plugins.Kubernetes.Components;
using SbConsole.Plugins.Kubernetes.Overview;
using SbConsole.Sdk;
using WorkloadsPage = SbConsole.Plugins.Kubernetes.Pages.Workloads;

namespace SbConsole.Plugins.Kubernetes.Tests.Pages;

public class WorkloadsPageTests : K8sPageTestBase
{
    private static readonly K8sWorkloadKind[] Kinds = Enum.GetValues<K8sWorkloadKind>();

    public WorkloadsPageTests()
    {
        foreach (var kind in Kinds)
        {
            ArrangeList(kind);
        }

        ArrangeCounts();
        Operations.GetScaleInfoAsync(Secret, Arg.Any<K8sWorkloadKind>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new K8sScaleInfo(4, 2, false, null, 500, 512L * 1024 * 1024));
    }

    private void ArrangeList(K8sWorkloadKind kind, params K8sWorkload[] rows) =>
        Operations.ListWorkloadsAsync(Secret, kind, Arg.Any<IReadOnlyList<string>>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => (IReadOnlyList<K8sWorkload>)rows);

    private void ArrangeCounts(Dictionary<K8sWorkloadKind, K8sKindCount>? counts = null) =>
        Operations.CountWorkloadsAsync(Secret, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => (IReadOnlyDictionary<K8sWorkloadKind, K8sKindCount>)(counts ?? Kinds.ToDictionary(k => k, _ => new K8sKindCount(0, false))));

    private int ListCalls(K8sWorkloadKind kind) =>
        Operations.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IKubernetesOperations.ListWorkloadsAsync)
                                              && (K8sWorkloadKind)c.GetArguments()[1]! == kind);

    private int CountCalls() =>
        Operations.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IKubernetesOperations.CountWorkloadsAsync));

    private static K8sWorkload Deploy(string ns, string name, int desired, int ready, params string[] images) =>
        TestData.Deployment(ns, name, desired, ready) with { Ready = ready, Images = images.Length == 0 ? [$"ghcr.io/nervecenter/{name}:1.0"] : images };

    private IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderPage(string? query = null)
    {
        if (query is not null)
        {
            Services.GetRequiredService<NavigationManager>().NavigateTo($"/p/kubernetes/workloads?{query}");
        }

        RenderFragment fragment = builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudDialogProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<WorkloadsPage>(2);
            builder.CloseComponent();
        };

        var cut = Render(fragment);
        cut.WaitForState(() => cut.FindAll(".k8s-wl-table, .k8s-wl-error").Count > 0, TimeSpan.FromSeconds(5));
        return cut;
    }

    private static AngleSharp.Dom.IElement Row(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string name) =>
        cut.FindAll(".k8s-wl-row").Single(r => r.QuerySelector(".k8s-wl-name")!.TextContent.Trim() == name);

    private static void OpenMenu(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string name) =>
        Row(cut, name).QuerySelector(".k8s-row-menu button")!.Click();

    private static void ClickMenuItem(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string text) =>
        cut.FindAll(".mud-menu-item").First(i => i.TextContent.Contains(text)).Click();

    private string Link(string href) => AttentionRules.WithConnection(ConnectionId)(href);

    [Fact]
    public void Deployments_tab_renders_rows_with_ready_bar_images_age_and_detail_link()
    {
        ArrangeList(K8sWorkloadKind.Deployment,
            Deploy("checkout", "payment-gateway", 4, 2, "ghcr.io/nervecenter/payment-gateway:2.14.1"),
            Deploy("checkout", "cart", 3, 3, "ghcr.io/nervecenter/cart:1.9.3", "redis:7.2-alpine"));
        var cut = RenderPage();

        var rows = cut.FindAll(".k8s-wl-row");
        rows.Should().HaveCount(2);
        var pg = Row(cut, "payment-gateway");
        pg.QuerySelector(".k8s-ready-text")!.TextContent.Should().Be("2/4");
        pg.TextContent.Should().Contain("checkout").And.Contain("ghcr.io/nervecenter/payment-gateway:2.14.1").And.Contain("1d");
        pg.QuerySelector(".k8s-wl-name")!.GetAttribute("href").Should().Be(Link(KubernetesPlugin.DeploymentHref("checkout", "payment-gateway")));
        Row(cut, "cart").QuerySelector(".k8s-wl-images")!.TextContent.Should().Be("ghcr.io/nervecenter/cart:1.9.3 · redis:7.2-alpine");
        pg.ClassList.Should().Contain("k8s-wl-unhealthy");
        Row(cut, "cart").ClassList.Should().NotContain("k8s-wl-unhealthy");
    }

    [Fact]
    public void Scaled_to_zero_rows_show_the_chip()
    {
        ArrangeList(K8sWorkloadKind.Deployment, Deploy("payments", "webhooks", 0, 0), Deploy("payments", "ledger", 3, 3));
        var cut = RenderPage();

        Row(cut, "webhooks").QuerySelector(".k8s-scaled-zero")!.TextContent.Trim().Should().Be("scaled to 0");
        Row(cut, "ledger").QuerySelector(".k8s-scaled-zero").Should().BeNull();
    }

    [Fact]
    public void Filter_matches_name_or_image_case_insensitively()
    {
        ArrangeList(K8sWorkloadKind.Deployment,
            Deploy("checkout", "cart", 3, 3, "ghcr.io/nervecenter/cart:1.9.3", "redis:7.2-alpine"),
            Deploy("checkout", "pricing", 2, 2),
            Deploy("payments", "ledger", 3, 3));
        var cut = RenderPage();

        cut.Find(".k8s-wl-filter input").Input("REDIS");
        cut.WaitForAssertion(() => cut.FindAll(".k8s-wl-row").Select(r => r.QuerySelector(".k8s-wl-name")!.TextContent.Trim())
            .Should().Equal("cart"));

        cut.Find(".k8s-wl-filter input").Input("Pric");
        cut.WaitForAssertion(() => cut.FindAll(".k8s-wl-row").Select(r => r.QuerySelector(".k8s-wl-name")!.TextContent.Trim())
            .Should().Equal("pricing"));
    }

    [Fact]
    public async Task Label_selector_is_applied_on_enter_and_reloads_with_it()
    {
        var cut = RenderPage();
        await Operations.Received(1).ListWorkloadsAsync(Secret, K8sWorkloadKind.Deployment, Arg.Any<IReadOnlyList<string>>(), null, Arg.Any<CancellationToken>());

        var input = cut.Find(".k8s-wl-labels input");
        input.Input("app=api");
        cut.Find(".k8s-wl-labels input").KeyDown("Enter");

        cut.WaitForAssertion(() => Operations.Received(1).ListWorkloadsAsync(Secret, K8sWorkloadKind.Deployment,
            Arg.Any<IReadOnlyList<string>>(), "app=api", Arg.Any<CancellationToken>()));
    }

    [Fact]
    public void Tabs_show_counts_and_a_failure_dot()
    {
        ArrangeCounts(new Dictionary<K8sWorkloadKind, K8sKindCount>
        {
            [K8sWorkloadKind.Deployment] = new(29, false), [K8sWorkloadKind.StatefulSet] = new(4, false),
            [K8sWorkloadKind.DaemonSet] = new(2, false), [K8sWorkloadKind.Job] = new(38, true), [K8sWorkloadKind.CronJob] = new(7, false),
        });
        var cut = RenderPage();

        cut.WaitForAssertion(() => cut.FindAll(".mud-tab").Select(t => t.TextContent.Trim()).Should()
            .Equal("Deployments 29", "StatefulSets 4", "DaemonSets 2", "Jobs 38", "CronJobs 7"));
        var tabs = cut.FindAll(".mud-tab");
        tabs[3].QuerySelectorAll(".k8s-tab-failure").Should().HaveCount(1);
        tabs[0].QuerySelectorAll(".k8s-tab-failure").Should().BeEmpty();
    }

    [Fact]
    public void Query_tab_jobs_opens_the_jobs_tab_with_status_and_reason()
    {
        ArrangeList(K8sWorkloadKind.Job, TestData.Job("payments", "settlement-20261014-0200"), TestData.Job("payments", "nightly", "Complete", null));
        var cut = RenderPage("tab=jobs");

        cut.WaitForAssertion(() => cut.FindAll(".k8s-wl-row").Should().HaveCount(2));
        cut.Find(".mud-tab.mud-tab-active").TextContent.Should().Contain("Jobs");
        ListCalls(K8sWorkloadKind.Deployment).Should().Be(0);
        var failed = Row(cut, "settlement-20261014-0200");
        failed.QuerySelector(".k8s-job-status")!.TextContent.Should().Contain("Failed").And.Contain("BackoffLimitExceeded");
        failed.ClassList.Should().Contain("k8s-wl-unhealthy");
        Row(cut, "nightly").QuerySelector(".k8s-job-status")!.TextContent.Should().Contain("Complete");
        // Jobs have no inline actions.
        cut.FindAll(".k8s-scale").Should().BeEmpty();
        cut.FindAll(".k8s-restart").Should().BeEmpty();
    }

    [Fact]
    public void Switching_tabs_loads_that_kind_and_keeps_the_query_in_sync()
    {
        ArrangeList(K8sWorkloadKind.StatefulSet, Deploy("data", "postgres", 3, 3) with { Kind = K8sWorkloadKind.StatefulSet });
        var cut = RenderPage();

        cut.FindAll(".mud-tab").First(t => t.TextContent.Contains("StatefulSets")).Click();

        cut.WaitForAssertion(() => cut.FindAll(".k8s-wl-name").Select(n => n.TextContent.Trim()).Should().Equal("postgres"));
        Services.GetRequiredService<NavigationManager>().Uri.Should().Contain("tab=statefulsets");
        ListCalls(K8sWorkloadKind.StatefulSet).Should().Be(1);
    }

    [Fact]
    public void Admin_sees_scale_restart_and_delete_and_no_rbac_alert()
    {
        ArrangeList(K8sWorkloadKind.Deployment, Deploy("checkout", "api", 3, 3));
        var cut = RenderPage();

        cut.WaitForAssertion(() => cut.FindAll(".k8s-scale").Should().HaveCount(1));
        cut.FindAll(".k8s-restart").Should().HaveCount(1);
        cut.FindAll(".k8s-rbac-alert").Should().BeEmpty();
        cut.FindAll(".k8s-rbac-chip").Should().BeEmpty();

        OpenMenu(cut, "api");
        cut.WaitForAssertion(() => cut.FindAll(".k8s-delete").Should().HaveCount(1));
        cut.FindAll(".k8s-menu-note").Should().BeEmpty();
        cut.FindAll(".mud-menu-item").Select(i => i.TextContent.Trim()).Should()
            .Equal("Open detail", "View YAML", "Copy kubectl command", "Delete…");
    }

    [Fact]
    public void Viewer_sees_no_mutating_controls_the_rbac_alert_and_the_menu_note()
    {
        Operations.GetAccessAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => TestData.ViewerAccess("sbconsole-viewer"));
        ArrangeList(K8sWorkloadKind.Deployment, Deploy("checkout", "api", 3, 3));
        var cut = RenderPage();

        cut.WaitForAssertion(() => cut.FindAll(".k8s-rbac-alert").Should().HaveCount(1));
        cut.Find(".k8s-rbac-alert").TextContent.Should().Contain(
            "Connected as sbconsole-viewer. Missing for this page: deployments/scale · patch, deployments · patch, deployments · delete, "
            + "statefulsets/scale · patch, statefulsets · patch, statefulsets · delete, daemonsets · patch, daemonsets · delete, "
            + "jobs · delete, cronjobs · delete. Reads work normally. Ask a cluster admin for more permissions if you need to act from here.");
        cut.FindAll(".k8s-rbac-chip").Should().HaveCount(1);
        cut.FindAll(".k8s-scale").Should().BeEmpty();
        cut.FindAll(".k8s-restart").Should().BeEmpty();

        OpenMenu(cut, "api");
        cut.WaitForAssertion(() => cut.FindAll(".k8s-menu-note").Should().HaveCount(1));
        cut.Find(".k8s-menu-note").TextContent.Trim().Should().Be("Scale, restart and delete need more permissions");
        cut.FindAll(".k8s-delete").Should().BeEmpty();
    }

    [Fact]
    public void DaemonSets_can_be_restarted_but_not_scaled()
    {
        ArrangeList(K8sWorkloadKind.DaemonSet, Deploy("platform", "log-shipper", 3, 3) with { Kind = K8sWorkloadKind.DaemonSet });
        var cut = RenderPage("tab=daemonsets");

        cut.WaitForAssertion(() => cut.FindAll(".k8s-restart").Should().HaveCount(1));
        cut.FindAll(".k8s-scale").Should().BeEmpty();
    }

    [Fact]
    public async Task Scale_opens_the_dialog_and_a_confirmed_scale_reloads_list_and_counts()
    {
        ArrangeList(K8sWorkloadKind.Deployment, Deploy("checkout", "api", 4, 2));
        Operations.ScaleAsync(Secret, K8sWorkloadKind.Deployment, "checkout", "api", 6, Arg.Any<CancellationToken>()).Returns(4);
        var cut = RenderPage();
        var lists = ListCalls(K8sWorkloadKind.Deployment);
        var counts = CountCalls();

        cut.Find(".k8s-scale").Click();
        cut.WaitForAssertion(() => cut.FindAll(".k8s-scale-summary").Should().HaveCount(1));
        cut.Find(".k8s-scale-replicas input").Input("6");
        cut.WaitForAssertion(() => cut.Find(".k8s-scale-confirm").TextContent.Trim().Should().Be("Scale to 6"));
        cut.Find(".k8s-scale-confirm").Click();

        cut.WaitForAssertion(() => cut.FindAll(".k8s-scale-dialog").Should().BeEmpty());
        await Operations.Received(1).ScaleAsync(Secret, K8sWorkloadKind.Deployment, "checkout", "api", 6, Arg.Any<CancellationToken>());
        cut.WaitForAssertion(() => ListCalls(K8sWorkloadKind.Deployment).Should().Be(lists + 1));
        CountCalls().Should().Be(counts + 1);
        Services.GetRequiredService<ISnackbar>().ShownSnackbars.Select(s => s.Message).Should().Contain("Scaled api to 6 replicas.");
    }

    [Fact]
    public async Task Restart_asks_a_plain_confirmation_then_restarts()
    {
        ArrangeList(K8sWorkloadKind.Deployment, Deploy("checkout", "api", 3, 3));
        Confirmation.ConfirmAsync("Restart", "api", false, Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(true);
        var cut = RenderPage();

        cut.Find(".k8s-restart").Click();

        cut.WaitForAssertion(() => Operations.Received(1).RestartAsync(Secret, K8sWorkloadKind.Deployment, "checkout", "api",
            Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()));
        await Confirmation.Received(1).ConfirmAsync("Restart", "api", false, Arg.Any<int?>(), Arg.Any<CancellationToken>());
        cut.WaitForAssertion(() => Services.GetRequiredService<ISnackbar>().ShownSnackbars.Select(s => s.Message)
            .Should().Contain("Restarting api."));
    }

    [Fact]
    public async Task Declined_restart_does_nothing()
    {
        ArrangeList(K8sWorkloadKind.Deployment, Deploy("checkout", "api", 3, 3));
        Confirmation.ConfirmAsync("Restart", "api", false, Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(false);
        var cut = RenderPage();

        cut.Find(".k8s-restart").Click();
        await Task.Delay(50);

        await Operations.DidNotReceiveWithAnyArgs().RestartAsync(default!, default, default!, default!, default, default);
    }

    [Fact]
    public async Task Delete_sends_a_destructive_prod_confirmation_then_deletes()
    {
        ArrangeList(K8sWorkloadKind.Deployment, Deploy("checkout", "api", 3, 2, "api:1", "sidecar:2"));
        ConfirmationRequest? asked = null;
        Confirmation.ConfirmAsync(Arg.Do<ConfirmationRequest>(r => asked = r), Arg.Any<CancellationToken>()).Returns(true);
        var cut = RenderPage();

        OpenMenu(cut, "api");
        cut.WaitForAssertion(() => cut.FindAll(".k8s-delete").Should().HaveCount(1));
        cut.Find(".k8s-delete").Click();

        cut.WaitForAssertion(() => Operations.Received(1).DeleteWorkloadAsync(Secret, K8sWorkloadKind.Deployment, "checkout", "api", Arg.Any<CancellationToken>()));
        asked.Should().NotBeNull();
        asked!.Verb.Should().Be("Delete Deployment");
        asked.Target.Should().Be("api");
        asked.IsProd.Should().BeTrue();
        asked.Destructive.Should().BeTrue();
        asked.AuditAction.Should().Be("k8s.deployment.delete");
        asked.Consequence.Should().Be("Deletes the Deployment and, in the background, everything it owns (ReplicaSets and Pods).");
        asked.Details.Should().Equal(
            new ConfirmationDetail("Namespace", "checkout"), new ConfirmationDetail("Ready", "2/3"), new ConfirmationDetail("Images", "api:1 · sidecar:2"));
        await Audit.Received(1).RecordAsync("k8s.deployment.delete", $"{ConnectionName}/checkout/api", ActionRisk.Destructive, true,
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Delete_on_a_non_prod_connection_is_not_flagged_prod()
    {
        Connections.ListAsync(KubernetesPlugin.Kind, Arg.Any<CancellationToken>()).Returns(
        [
            new ConnectionInfo(ConnectionId, ConnectionName, KubernetesPlugin.Kind, ["staging"], true, Time.GetUtcNow(), null, Time.GetUtcNow(),
                new Dictionary<string, string> { ["Server"] = "aks-stg.example", ["Context"] = "aks-stg", ["TLS"] = "verified" }),
        ]);
        ArrangeList(K8sWorkloadKind.Deployment, Deploy("checkout", "api", 3, 3));
        ConfirmationRequest? asked = null;
        Confirmation.ConfirmAsync(Arg.Do<ConfirmationRequest>(r => asked = r), Arg.Any<CancellationToken>()).Returns(true);
        var cut = RenderPage();

        OpenMenu(cut, "api");
        cut.WaitForAssertion(() => cut.FindAll(".k8s-delete").Should().HaveCount(1));
        cut.Find(".k8s-delete").Click();

        cut.WaitForAssertion(() => asked.Should().NotBeNull());
        asked!.IsProd.Should().BeFalse();
        asked.Destructive.Should().BeTrue();
    }

    [Fact]
    public void Row_menu_divider_only_precedes_delete()
    {
        ArrangeList(K8sWorkloadKind.Deployment, Deploy("checkout", "api", 3, 3));
        var cut = RenderPage();
        OpenMenu(cut, "api");
        cut.WaitForAssertion(() => cut.FindAll(".k8s-delete").Should().HaveCount(1));
        cut.FindAll(".mud-popover .mud-divider").Should().HaveCount(1);
    }

    [Fact]
    public void Viewer_row_menu_has_no_divider()
    {
        Operations.GetAccessAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => TestData.ViewerAccess());
        ArrangeList(K8sWorkloadKind.Deployment, Deploy("checkout", "api", 3, 3));
        var cut = RenderPage();
        OpenMenu(cut, "api");
        cut.WaitForAssertion(() => cut.FindAll(".k8s-menu-note").Should().HaveCount(1));
        cut.FindAll(".mud-popover .mud-divider").Should().BeEmpty();
    }

    [Fact]
    public async Task Tab_switch_before_the_first_load_finishes_still_fetches_permissions_and_counts()
    {
        var gate = new TaskCompletionSource<K8sAccessFacts>();
        Operations.GetAccessAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => gate.Task);
        ArrangeList(K8sWorkloadKind.Deployment, Deploy("checkout", "api", 3, 3));
        ArrangeList(K8sWorkloadKind.StatefulSet, TestData.Deployment("checkout", "postgres", 1, 1) with { Kind = K8sWorkloadKind.StatefulSet });
        ArrangeCounts(Kinds.ToDictionary(k => k, _ => new K8sKindCount(7, false)));

        RenderFragment fragment = builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudDialogProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<WorkloadsPage>(2);
            builder.CloseComponent();
        };
        var cut = Render(fragment);
        cut.WaitForState(() => Operations.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IKubernetesOperations.GetAccessAsync)),
            TimeSpan.FromSeconds(5));

        cut.FindAll(".mud-tab")[1].Click();
        await Task.Delay(50);
        gate.SetResult(TestData.AdminAccess());

        cut.WaitForAssertion(() => cut.FindAll(".k8s-wl-row").Should().HaveCount(1), TimeSpan.FromSeconds(5));
        cut.WaitForAssertion(() => cut.FindAll(".k8s-scale").Should().HaveCount(1), TimeSpan.FromSeconds(5));
        cut.WaitForAssertion(() => cut.FindAll(".mud-tab").Select(t => t.TextContent).Should().Contain(t => t.Contains('7')),
            TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Declined_delete_does_not_call_the_handler()
    {
        ArrangeList(K8sWorkloadKind.Deployment, Deploy("checkout", "api", 3, 3));
        Confirmation.ConfirmAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>()).Returns(false);
        var cut = RenderPage();

        OpenMenu(cut, "api");
        cut.WaitForAssertion(() => cut.FindAll(".k8s-delete").Should().HaveCount(1));
        cut.Find(".k8s-delete").Click();
        await Task.Delay(50);

        await Confirmation.Received(1).ConfirmAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>());
        await Operations.DidNotReceiveWithAnyArgs().DeleteWorkloadAsync(default!, default, default!, default!, default);
    }

    [Fact]
    public void Fifty_one_rows_render_fifty_then_show_all()
    {
        ArrangeList(K8sWorkloadKind.Deployment,
            Enumerable.Range(1, 51).Select(i => Deploy("checkout", $"svc-{i:00}", 1, 1)).ToArray());
        var cut = RenderPage();

        cut.FindAll(".k8s-wl-row").Should().HaveCount(50);
        cut.Find(".k8s-wl-more").TextContent.Trim().Should().Be("1 more in these namespaces · show all");

        cut.Find(".k8s-wl-show-all").Click();

        cut.FindAll(".k8s-wl-row").Should().HaveCount(51);
        cut.FindAll(".k8s-wl-more").Should().BeEmpty();
    }

    [Fact]
    public void Copy_kubectl_command_copies_the_get_yaml_command()
    {
        var module = JSInterop.SetupModule(K8sJs.ModulePath);
        module.SetupVoid("copyText", "kubectl -n checkout get deployment api -o yaml");
        ArrangeList(K8sWorkloadKind.Deployment, Deploy("checkout", "api", 3, 3));
        var cut = RenderPage();

        OpenMenu(cut, "api");
        cut.WaitForAssertion(() => cut.FindAll(".mud-menu-item").Should().NotBeEmpty());
        ClickMenuItem(cut, "Copy kubectl command");

        cut.WaitForAssertion(() => module.VerifyInvoke("copyText").Arguments.Should().Equal("kubectl -n checkout get deployment api -o yaml"));
    }

    [Fact]
    public void Non_deployment_names_open_the_yaml_dialog()
    {
        Operations.GetYamlAsync(Secret, K8sResourceKind.StatefulSet, "data", "postgres", Arg.Any<CancellationToken>())
            .Returns("kind: StatefulSet\n");
        ArrangeList(K8sWorkloadKind.StatefulSet, Deploy("data", "postgres", 3, 3) with { Kind = K8sWorkloadKind.StatefulSet });
        var cut = RenderPage("tab=statefulsets");

        cut.WaitForAssertion(() => cut.FindAll(".k8s-wl-row").Should().HaveCount(1));
        cut.Find("button.k8s-wl-name").Click();

        cut.WaitForAssertion(() => cut.Find("pre.k8s-yaml").TextContent.Should().Contain("kind: StatefulSet"));
    }

    [Fact]
    public void Poller_reloads_every_ten_seconds()
    {
        var cut = RenderPage();
        var lists = ListCalls(K8sWorkloadKind.Deployment);

        Time.Advance(TimeSpan.FromSeconds(10));

        cut.WaitForAssertion(() => ListCalls(K8sWorkloadKind.Deployment).Should().Be(lists + 1));
    }

    [Fact]
    public async Task Poller_pauses_while_a_dialog_is_open()
    {
        ArrangeList(K8sWorkloadKind.Deployment, Deploy("checkout", "api", 4, 2));
        var cut = RenderPage();
        var lists = ListCalls(K8sWorkloadKind.Deployment);

        cut.Find(".k8s-scale").Click();
        cut.WaitForAssertion(() => cut.FindAll(".k8s-scale-summary").Should().HaveCount(1));
        Time.Advance(TimeSpan.FromSeconds(30));
        await Task.Delay(100);
        ListCalls(K8sWorkloadKind.Deployment).Should().Be(lists);

        cut.Find(".k8s-scale-cancel").Click();
        cut.WaitForAssertion(() => cut.FindAll(".k8s-scale-dialog").Should().BeEmpty());
        Time.Advance(TimeSpan.FromSeconds(10));
        cut.WaitForAssertion(() => ListCalls(K8sWorkloadKind.Deployment).Should().Be(lists + 1));
    }

    [Fact]
    public void A_failed_list_shows_the_error_with_retry()
    {
        Operations.ListWorkloadsAsync(Secret, K8sWorkloadKind.Deployment, Arg.Any<IReadOnlyList<string>>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<K8sWorkload>>(_ => throw new HttpRequestException("No such host is known. (k.example:6443)"));
        var cut = RenderPage();

        cut.WaitForAssertion(() => cut.FindAll(".k8s-wl-error").Should().HaveCount(1));

        ArrangeList(K8sWorkloadKind.Deployment, Deploy("checkout", "api", 3, 3));
        cut.Find(".k8s-wl-retry").Click();

        cut.WaitForAssertion(() => cut.FindAll(".k8s-wl-row").Should().HaveCount(1));
        cut.FindAll(".k8s-wl-error").Should().BeEmpty();
    }

    [Fact]
    public void Footnote_explains_risk_levels_and_links_the_audit_log()
    {
        var cut = RenderPage();

        var note = cut.Find(".k8s-wl-footnote");
        note.TextContent.Should().Contain("Scale and Restart are Mutating: a short dialog, no typing. Delete is Destructive and only in the menu.")
            .And.Contain("All actions audited · Audit log");
        note.QuerySelector("a")!.GetAttribute("href").Should().Be("/audit");
    }
}
