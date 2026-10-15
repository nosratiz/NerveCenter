using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Plugins.Kubernetes.Components;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes.Tests.Pages;

public class ScaleDialogTests : K8sPageTestBase
{
    private const long HalfCore = 500;
    private const long HalfGi = 512L * 1024 * 1024;

    private void ArrangeInfo(int replicas = 4, int ready = 2, bool hpa = false, string? hpaName = null) =>
        Operations.GetScaleInfoAsync(Secret, K8sWorkloadKind.Deployment, "checkout", "payment-gateway", Arg.Any<CancellationToken>())
            .Returns(new K8sScaleInfo(replicas, ready, hpa, hpaName, HalfCore, HalfGi));

    private async Task<(IRenderedComponent<MudDialogProvider> Cut, IDialogReference Reference)> ShowAsync()
    {
        var cut = Render<MudDialogProvider>();
        var dialogs = Services.GetRequiredService<IDialogService>();
        var connection = (await Connections.ListAsync(KubernetesPlugin.Kind))[0];
        IDialogReference reference = null!;
        await cut.InvokeAsync(async () => reference = await ScaleDialog.ShowAsync(dialogs, connection,
            K8sWorkloadKind.Deployment, "checkout", "payment-gateway"));
        cut.WaitForAssertion(() => cut.FindAll(".k8s-scale-summary").Should().HaveCount(1));
        return (cut, reference);
    }

    private static void SetReplicas(IRenderedComponent<MudDialogProvider> cut, int value) =>
        cut.Find(".k8s-scale-replicas input").Input(value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public async Task Shows_the_current_and_ready_replicas_and_that_the_change_sticks_without_an_HPA()
    {
        ArrangeInfo();
        var (cut, _) = await ShowAsync();

        cut.Markup.Should().Contain("Scale").And.Contain("payment-gateway");
        cut.Find(".k8s-scale-summary").TextContent.Should().Contain("checkout · currently 4 replicas, 2 ready.")
            .And.Contain("HPA is not attached, so this sticks.");
        cut.FindAll(".k8s-scale-hpa").Should().BeEmpty();
        cut.Find(".k8s-scale-audit").TextContent.Should().Be("Audited as k8s.deployment.scale");
        cut.Find(".k8s-scale-replicas input").GetAttribute("value").Should().Be("4");
    }

    [Fact]
    public async Task Warns_when_an_HPA_manages_the_replicas()
    {
        ArrangeInfo(hpa: true, hpaName: "checkout-api");
        var (cut, _) = await ShowAsync();

        cut.Find(".k8s-scale-hpa").TextContent.Trim().Should()
            .Be("An HPA (checkout-api) manages replicas — this change will be overwritten.");
        cut.Find(".k8s-scale-summary").TextContent.Should().NotContain("this sticks");
    }

    [Fact]
    public async Task Hint_multiplies_per_pod_requests_by_the_delta()
    {
        ArrangeInfo();
        var (cut, _) = await ShowAsync();
        cut.FindAll(".k8s-scale-hint").Should().BeEmpty();

        SetReplicas(cut, 6);
        cut.WaitForAssertion(() => cut.Find(".k8s-scale-hint").TextContent.Trim().Should().Be("+2 · needs 1 CPU, 1 Gi more requests"));

        SetReplicas(cut, 3);
        cut.WaitForAssertion(() => cut.Find(".k8s-scale-hint").TextContent.Trim().Should().Be("−1 · frees 0.5 CPU, 512 Mi requests"));
    }

    [Fact]
    public async Task Stepper_buttons_change_the_target_and_the_confirm_label()
    {
        ArrangeInfo();
        var (cut, _) = await ShowAsync();
        cut.Find(".k8s-scale-confirm").HasAttribute("disabled").Should().BeTrue();
        cut.Find(".k8s-scale-confirm").TextContent.Trim().Should().Be("Scale to 4");

        cut.Find(".k8s-scale-plus").Click();
        cut.Find(".k8s-scale-plus").Click();
        cut.Find(".k8s-scale-minus").Click();

        cut.Find(".k8s-scale-confirm").TextContent.Trim().Should().Be("Scale to 5");
        cut.Find(".k8s-scale-confirm").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public async Task Confirm_calls_the_handler_and_closes_with_the_new_count()
    {
        ArrangeInfo();
        Operations.ScaleAsync(Secret, K8sWorkloadKind.Deployment, "checkout", "payment-gateway", 6, Arg.Any<CancellationToken>()).Returns(4);
        var (cut, reference) = await ShowAsync();

        SetReplicas(cut, 6);
        cut.WaitForAssertion(() => cut.Find(".k8s-scale-confirm").TextContent.Trim().Should().Be("Scale to 6"));
        cut.Find(".k8s-scale-confirm").Click();

        var result = await reference.Result;
        result!.Canceled.Should().BeFalse();
        result.Data.Should().Be(6);
        await Operations.Received(1).ScaleAsync(Secret, K8sWorkloadKind.Deployment, "checkout", "payment-gateway", 6, Arg.Any<CancellationToken>());
        await Audit.Received(1).RecordAsync("k8s.deployment.scale", $"{ConnectionName}/checkout/payment-gateway", ActionRisk.Mutating, true,
            "4 → 6 replicas", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_scale_stays_open_and_shows_the_reason()
    {
        ArrangeInfo();
        Operations.ScaleAsync(Secret, K8sWorkloadKind.Deployment, "checkout", "payment-gateway", 5, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));
        var (cut, reference) = await ShowAsync();

        cut.Find(".k8s-scale-plus").Click();
        cut.Find(".k8s-scale-confirm").Click();

        cut.WaitForAssertion(() => cut.FindAll(".k8s-scale-dialog .k8s-error-alert").Should().HaveCount(1));
        reference.Result.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task Cancel_closes_without_scaling()
    {
        ArrangeInfo();
        var (cut, reference) = await ShowAsync();

        cut.Find(".k8s-scale-cancel").Click();

        (await reference.Result)!.Canceled.Should().BeTrue();
        await Operations.DidNotReceiveWithAnyArgs().ScaleAsync(default!, default, default!, default!, default, default);
    }
}
