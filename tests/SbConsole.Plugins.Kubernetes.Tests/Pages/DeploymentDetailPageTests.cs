using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using NSubstitute;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Sdk;
using DetailPage = SbConsole.Plugins.Kubernetes.Pages.DeploymentDetail;

namespace SbConsole.Plugins.Kubernetes.Tests.Pages;

public class DeploymentDetailPageTests : K8sPageTestBase
{
    public DeploymentDetailPageTests()
    {
        Operations.GetDeploymentDetailAsync(Secret, "checkout", "payment-gateway", Arg.Any<CancellationToken>())
            .Returns(_ => TestData.PaymentGatewayDetail());
        Operations.RollbackDeploymentAsync(Secret, "checkout", "payment-gateway", Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(23L);
        Operations.GetScaleInfoAsync(Secret, K8sWorkloadKind.Deployment, "checkout", "payment-gateway", Arg.Any<CancellationToken>())
            .Returns(new K8sScaleInfo(4, 2, false, null, 500, 512L * 1024 * 1024));
        Confirmation.ConfirmAsync("Roll back to revision", Arg.Any<string>(), false, Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(true);
    }

    private int DetailCalls() =>
        Operations.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IKubernetesOperations.GetDeploymentDetailAsync));

    private IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderPage(
        string name = "payment-gateway", string? query = null, string ready = ".k8s-facts, .k8s-dd-gone, .k8s-dd-error, .k8s-dd-noconn")
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo(
            $"{KubernetesPlugin.DeploymentHref("checkout", name)}?{query ?? "connectionId=" + ConnectionId}");

        RenderFragment fragment = builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudDialogProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<DetailPage>(2);
            builder.AddAttribute(3, nameof(DetailPage.Namespace), "checkout");
            builder.AddAttribute(4, nameof(DetailPage.Name), name);
            builder.CloseComponent();
        };

        var cut = Render(fragment);
        cut.WaitForState(() => cut.FindAll(ready).Count > 0, TimeSpan.FromSeconds(5));
        return cut;
    }

    private static string Text(AngleSharp.Dom.IElement e) => string.Join(" ", e.TextContent.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries));

    [Fact]
    public void Header_has_breadcrumbs_kind_and_prod_chips()
    {
        var cut = RenderPage();

        var crumbs = cut.Find(".k8s-breadcrumbs");
        Text(crumbs).Should().Contain(ConnectionName).And.Contain("checkout").And.Contain("payment-gateway");
        var links = crumbs.QuerySelectorAll("a").Select(a => a.GetAttribute("href")).ToList();
        links.Should().Contain($"{KubernetesPlugin.WorkloadsHref}?connectionId={ConnectionId}");
        links.Should().Contain($"{KubernetesPlugin.WorkloadsHref}?connectionId={ConnectionId}&ns=checkout");
        cut.Find(".k8s-kind-chip").TextContent.Trim().Should().Be("Deployment");
        cut.FindAll(".k8s-prod-chip").Should().HaveCount(1);
        cut.FindAll(".k8s-dd-yaml").Should().HaveCount(1);
        cut.FindAll(".k8s-dd-scale").Should().HaveCount(1);
        cut.FindAll(".k8s-dd-restart").Should().HaveCount(1);
    }

    [Fact]
    public void Stuck_alert_explains_the_rollout_and_offers_the_serving_revision()
    {
        var cut = RenderPage();

        Text(cut.Find(".k8s-stuck-alert")).Should().Contain(
            "Rollout stuck. Revision 23 has been progressing for 38m; 2 of the new pods are crash-looping on OOM. Revision 22 is still serving 2 pods.");
        cut.Find(".k8s-rollback-stuck").TextContent.Trim().Should().Be("Rollback to 22");
    }

    [Fact]
    public async Task Stuck_rollback_button_rolls_back_to_the_serving_revision()
    {
        var cut = RenderPage();
        var loads = DetailCalls();

        cut.Find(".k8s-rollback-stuck").Click();

        cut.WaitForAssertion(() => Operations.Received(1).RollbackDeploymentAsync(Secret, "checkout", "payment-gateway", 22, Arg.Any<CancellationToken>()));
        await Confirmation.Received(1).ConfirmAsync("Roll back to revision", "22", false, Arg.Any<int?>(), Arg.Any<CancellationToken>());
        await Audit.Received(1).RecordAsync("k8s.deployment.rollback", $"{ConnectionName}/checkout/payment-gateway", ActionRisk.Mutating, true,
            "rev 23 → rev 22", Arg.Any<CancellationToken>());
        cut.WaitForAssertion(() => DetailCalls().Should().BeGreaterThan(loads));
    }

    [Fact]
    public void Healthy_deployment_has_no_stuck_alert()
    {
        var detail = TestData.PaymentGatewayDetail();
        Operations.GetDeploymentDetailAsync(Secret, "checkout", "payment-gateway", Arg.Any<CancellationToken>())
            .Returns(detail with { Deployment = detail.Deployment with { Conditions = [new K8sCondition("Progressing", "True", "NewReplicaSetAvailable", null, TestData.T0)] } });
        var cut = RenderPage();

        cut.FindAll(".k8s-stuck-alert").Should().BeEmpty();
    }

    [Fact]
    public void Facts_show_ready_strategy_image_resources_selector_service_and_age()
    {
        var cut = RenderPage();

        var facts = Text(cut.Find(".k8s-facts"));
        cut.Find(".k8s-facts .k8s-ready-text").TextContent.Should().Be("2/4");
        facts.Should().Contain("desired")
            .And.Contain("RollingUpdate").And.Contain("maxSurge 25% · maxUnavailable 0")
            .And.Contain("payment-gateway:2.14.1").And.Contain("was 2.13.4")
            .And.Contain("500m · 512 Mi").And.Contain("limit 512 Mi").And.Contain("OOMKilled at 512 Mi")
            .And.Contain("app=payment-gateway").And.Contain("payment-gateway · 2 endpoints")
            .And.Contain("41d").And.Contain("23 revisions");
    }

    [Fact]
    public void Facts_omit_was_and_oom_when_they_do_not_apply()
    {
        var detail = TestData.PaymentGatewayDetail();
        Operations.GetDeploymentDetailAsync(Secret, "checkout", "payment-gateway", Arg.Any<CancellationToken>())
            .Returns(detail with
            {
                ReplicaSets = [detail.ReplicaSets[0], detail.ReplicaSets[1] with { Images = detail.ReplicaSets[0].Images }],
                Pods = detail.Pods.Select(p => p with { LastTerminationReason = null }).ToList(),
                Services = [],
            });
        var cut = RenderPage();

        var facts = Text(cut.Find(".k8s-facts"));
        facts.Should().NotContain("was ").And.NotContain("OOMKilled").And.Contain("Service none");
    }

    [Fact]
    public void Strip_shows_live_replica_sets_with_pod_chips_and_collapses_older_ones()
    {
        var cut = RenderPage();

        var cards = cut.FindAll(".k8s-rs-card");
        cards.Should().HaveCount(2);
        Text(cards[0]).Should().Contain("payment-gateway-7d9f8").And.Contain("rev 23 · current").And.Contain("2.14.1")
            .And.Contain("desired 4 · ready 0 · 38m");
        Text(cards[1]).Should().Contain("payment-gateway-5c6b4").And.Contain("rev 22 · old").And.Contain("2.13.4")
            .And.Contain("desired 2 · ready 2 · 9d");
        Text(cut.Find(".k8s-rs-older")).Should().Be("3 older · rev 19–21 · desired 0 · no pods · kept by revisionHistoryLimit 10");

        var chips = cut.FindAll(".k8s-rs-strip .k8s-phase-chip");
        chips.Select(c => Text(c)).Should().Equal("x2k4q ↻14", "p7m2w ↻11", "k8s2d", "nq41v");
        chips[0].GetAttribute("href").Should().Be(KubernetesPlugin.PodHref("checkout", "payment-gateway-7d9f8-x2k4q") + "?connectionId=" + ConnectionId);
        Text(cut.Find(".k8s-rs-legend")).Should().Contain("running").And.Contain("pending").And.Contain("crash loop").And.Contain("terminating");
    }

    [Fact]
    public void Conditions_and_events_are_listed()
    {
        var cut = RenderPage();

        var conditions = Text(cut.Find(".k8s-conditions"));
        conditions.Should().Contain("Available").And.Contain("Progressing").And.Contain("ProgressDeadlineExceeded · 28m");
        var events = cut.FindAll(".k8s-events .k8s-event-row");
        events.Should().HaveCount(2);
        Text(events[0]).Should().Contain("2m").And.Contain("BackOff").And.Contain("x2k4q").And.Contain("Back-off restarting failed container gateway").And.Contain("×14");
    }

    [Fact]
    public void History_lists_revisions_with_stuck_current_and_rollback_buttons()
    {
        var cut = RenderPage();

        var rows = cut.FindAll(".k8s-history-row");
        rows.Should().HaveCount(5);
        Text(rows[0]).Should().Contain("23").And.Contain("2.14.1").And.Contain("38m · bump sdk").And.Contain("stuck");
        rows[0].QuerySelector(".k8s-rollback").Should().BeNull();
        Text(rows[1]).Should().Contain("22").And.Contain("2.13.4").And.Contain("9d");
        rows[1].QuerySelector(".k8s-rollback").Should().NotBeNull();
        Text(rows[3]).Should().Contain("24d · manual");
    }

    [Fact]
    public async Task History_rollback_confirms_and_rolls_back_to_that_revision()
    {
        var cut = RenderPage();

        cut.FindAll(".k8s-history-row")[2].QuerySelector(".k8s-rollback")!.Click();

        cut.WaitForAssertion(() => Operations.Received(1).RollbackDeploymentAsync(Secret, "checkout", "payment-gateway", 21, Arg.Any<CancellationToken>()));
        await Confirmation.Received(1).ConfirmAsync("Roll back to revision", "21", false, Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Declined_rollback_does_nothing()
    {
        Confirmation.ConfirmAsync("Roll back to revision", Arg.Any<string>(), false, Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(false);
        var cut = RenderPage();

        cut.Find(".k8s-rollback-stuck").Click();

        await Task.Delay(50);
        await Operations.DidNotReceiveWithAnyArgs().RollbackDeploymentAsync(default!, default!, default!, default, default);
    }

    [Fact]
    public void Viewer_sees_no_scale_restart_rollback_or_delete()
    {
        Operations.GetAccessAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => TestData.ViewerAccess());
        var cut = RenderPage();

        cut.WaitForAssertion(() => cut.FindAll(".k8s-dd-yaml").Should().HaveCount(1));
        cut.FindAll(".k8s-dd-scale").Should().BeEmpty();
        cut.FindAll(".k8s-dd-restart").Should().BeEmpty();
        cut.FindAll(".k8s-rollback-stuck").Should().BeEmpty();
        cut.FindAll(".k8s-rollback").Should().BeEmpty();
        cut.FindAll(".k8s-dd-menu").Should().BeEmpty();
        cut.FindAll(".k8s-stuck-alert").Should().HaveCount(1);
    }

    [Fact]
    public void Missing_deployment_says_it_no_longer_exists()
    {
        Operations.GetDeploymentDetailAsync(Secret, "checkout", "x", Arg.Any<CancellationToken>()).Returns((K8sDeploymentDetail?)null);
        var cut = RenderPage("x");

        cut.Find(".k8s-dd-gone").TextContent.Should().Contain("Deployment checkout/x no longer exists.");
        cut.FindAll(".k8s-facts").Should().BeEmpty();
    }

    [Fact]
    public void Missing_connection_id_shows_a_link_back_to_workloads()
    {
        var cut = RenderPage(query: "");

        var message = cut.Find(".k8s-dd-noconn");
        message.QuerySelector("a")!.GetAttribute("href").Should().Be(KubernetesPlugin.WorkloadsHref);
        Operations.DidNotReceiveWithAnyArgs().GetDeploymentDetailAsync(default!, default!, default!, default);
    }

    [Fact]
    public void Unknown_connection_id_shows_the_same_message()
    {
        var cut = RenderPage(query: "connectionId=" + Guid.NewGuid());

        cut.FindAll(".k8s-dd-noconn").Should().HaveCount(1);
    }

    [Fact]
    public void A_failed_load_shows_the_error_with_retry()
    {
        Operations.GetDeploymentDetailAsync(Secret, "checkout", "payment-gateway", Arg.Any<CancellationToken>())
            .Returns<K8sDeploymentDetail?>(_ => throw new HttpRequestException("boom"));
        var cut = RenderPage();

        cut.FindAll(".k8s-dd-error").Should().HaveCount(1);
        Operations.GetDeploymentDetailAsync(Secret, "checkout", "payment-gateway", Arg.Any<CancellationToken>())
            .Returns(_ => TestData.PaymentGatewayDetail());
        cut.Find(".k8s-dd-retry").Click();

        cut.WaitForAssertion(() => cut.FindAll(".k8s-facts").Should().HaveCount(1));
        cut.FindAll(".k8s-dd-error").Should().BeEmpty();
    }

    [Fact]
    public void Restart_confirms_plainly_and_restarts_the_deployment()
    {
        Confirmation.ConfirmAsync("Restart", "payment-gateway", false, Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(true);
        var cut = RenderPage();

        cut.Find(".k8s-dd-restart").Click();

        cut.WaitForAssertion(() => Operations.Received(1).RestartAsync(Secret, K8sWorkloadKind.Deployment, "checkout", "payment-gateway",
            Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()));
    }

    [Fact]
    public void Scale_opens_the_scale_dialog()
    {
        var cut = RenderPage();

        cut.Find(".k8s-dd-scale").Click();

        cut.WaitForAssertion(() => cut.FindAll(".k8s-scale-summary").Should().HaveCount(1));
    }

    [Fact]
    public void Delete_asks_a_destructive_prod_confirmation_and_navigates_back_to_workloads()
    {
        ConfirmationRequest? asked = null;
        Confirmation.ConfirmAsync(Arg.Do<ConfirmationRequest>(r => asked = r), Arg.Any<CancellationToken>()).Returns(true);
        var cut = RenderPage();

        cut.Find(".k8s-dd-menu button").Click();
        cut.WaitForAssertion(() => cut.FindAll(".k8s-dd-delete").Should().HaveCount(1));
        cut.Find(".k8s-dd-delete").Click();

        cut.WaitForAssertion(() => Operations.Received(1).DeleteWorkloadAsync(Secret, K8sWorkloadKind.Deployment, "checkout", "payment-gateway", Arg.Any<CancellationToken>()));
        asked!.Verb.Should().Be("Delete Deployment");
        asked.Target.Should().Be("payment-gateway");
        asked.IsProd.Should().BeTrue();
        asked.Destructive.Should().BeTrue();
        asked.AuditAction.Should().Be("k8s.deployment.delete");
        var nav = Services.GetRequiredService<NavigationManager>();
        cut.WaitForAssertion(() => nav.Uri.Should().EndWith($"{KubernetesPlugin.WorkloadsHref}?connectionId={ConnectionId}&ns=checkout"));
    }

    [Fact]
    public void Yaml_opens_the_yaml_dialog()
    {
        Operations.GetYamlAsync(Secret, K8sResourceKind.Deployment, "checkout", "payment-gateway", Arg.Any<CancellationToken>())
            .Returns("kind: Deployment\n");
        var cut = RenderPage();

        cut.Find(".k8s-dd-yaml").Click();

        cut.WaitForAssertion(() => cut.Find("pre.k8s-yaml").TextContent.Should().Contain("kind: Deployment"));
    }

    [Fact]
    public void Poller_reloads_every_ten_seconds()
    {
        var cut = RenderPage();
        var loads = DetailCalls();

        Time.Advance(TimeSpan.FromSeconds(10));

        cut.WaitForAssertion(() => DetailCalls().Should().Be(loads + 1));
    }
}
