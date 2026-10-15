using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Plugins.Kubernetes.Components;

namespace SbConsole.Plugins.Kubernetes.Tests.Pages;

public class SmallComponentTests : K8sPageTestBase
{
    private const string Yaml = """
        apiVersion: apps/v1
        kind: Deployment
        metadata:
          name: checkout-api
          namespace: checkout
        spec:
          replicas: 3
        """;

    [Theory]
    [InlineData(3, 3, Color.Success)]
    [InlineData(1, 3, Color.Warning)]
    [InlineData(0, 3, Color.Error)]
    [InlineData(0, 0, Color.Default)]
    public void ReadyBar_colour_follows_ready_versus_desired(int ready, int desired, Color expected)
    {
        var cut = Render<ReadyBar>(p => p.Add(c => c.Ready, ready).Add(c => c.Desired, desired));

        cut.Find(".k8s-ready-text").TextContent.Should().Be($"{ready}/{desired}");
        var bar = cut.FindComponent<MudProgressLinear>();
        bar.Instance.Color.Should().Be(expected);
        bar.Instance.Class.Should().Contain("k8s-ready-bar");
    }

    [Theory]
    [InlineData("Running", Color.Success)]
    [InlineData("Pending", Color.Warning)]
    [InlineData("ContainerCreating", Color.Warning)]
    [InlineData("CrashLoopBackOff", Color.Error)]
    [InlineData("Error", Color.Error)]
    [InlineData("OOMKilled", Color.Error)]
    [InlineData("ImagePullBackOff", Color.Error)]
    [InlineData("Failed", Color.Error)]
    [InlineData("Terminating", Color.Default)]
    [InlineData("Completed", Color.Info)]
    [InlineData("Succeeded", Color.Info)]
    public void PhaseChip_colour_follows_status(string status, Color expected)
    {
        var cut = Render<PhaseChip>(p => p.Add(c => c.Status, status).Add(c => c.Label, "api-7d9f"));

        cut.FindComponent<MudChip<string>>().Instance.Color.Should().Be(expected);
        cut.Find(".k8s-phase-chip").TextContent.Trim().Should().Be("api-7d9f");
    }

    [Fact]
    public void PhaseChip_terminating_is_outlined_and_restarts_add_a_suffix()
    {
        var cut = Render<PhaseChip>(p => p.Add(c => c.Status, "Terminating").Add(c => c.Label, "api-7d9f").Add(c => c.Restarts, 4));

        cut.FindComponent<MudChip<string>>().Instance.Variant.Should().Be(Variant.Outlined);
        cut.Find(".k8s-phase-chip").TextContent.Trim().Should().Be("api-7d9f ↻4");
    }

    [Fact]
    public void PhaseChip_links_when_given_an_href()
    {
        var cut = Render<PhaseChip>(p => p.Add(c => c.Status, "Running").Add(c => c.Label, "api").Add(c => c.Href, "/p/kubernetes/pods/checkout/api"));

        cut.FindComponent<MudChip<string>>().Instance.Href.Should().Be("/p/kubernetes/pods/checkout/api");
    }

    [Fact]
    public void ErrorAlert_shows_the_message_and_the_http_status_behind_details()
    {
        var cut = Render<K8sErrorAlert>(p => p
            .Add(c => c.Info, new K8sErrorInfo("Your account can't list pods.", 403, "Forbidden", false))
            .Add(c => c.Title, "Couldn't load"));

        cut.Find(".k8s-error-title").TextContent.Should().Be("Couldn't load");
        cut.Find(".k8s-error-message").TextContent.Should().Be("Your account can't list pods.");
        cut.Find(".k8s-error-details-toggle").Click();
        cut.Find(".k8s-error-details").TextContent.Should().Contain("HTTP 403").And.Contain("Forbidden");
    }

    [Fact]
    public void ErrorAlert_without_a_status_has_no_details()
    {
        var cut = Render<K8sErrorAlert>(p => p.Add(c => c.Info, new K8sErrorInfo("Can't reach k.example.", null, null, true)));

        cut.FindAll(".k8s-error-details-toggle").Should().BeEmpty();
    }

    [Fact]
    public void StatTile_renders_label_value_and_stale_text()
    {
        var cut = Render<K8sStatTile>(p => p.Add(c => c.Label, "Pods").Add(c => c.Value, "31").Add(c => c.Caption, "running").Add(c => c.Stale, "stale · 11:58"));

        cut.Find(".k8s-tile-label").TextContent.Should().Be("Pods");
        cut.Find(".k8s-tile-number").TextContent.Should().Be("31");
        cut.Find(".k8s-tile").ClassList.Should().Contain("k8s-stale");
        cut.Find(".k8s-tile-caption").TextContent.Should().Be("stale · 11:58");
    }

    [Fact]
    public void PageHeader_renders_title_and_links_the_stylesheet()
    {
        var cut = Render<K8sPageHeader>(p => p.Add(c => c.Title, "Workloads"));

        cut.Find(".k8s-title").TextContent.Should().Be("Workloads");
        cut.FindComponent<Microsoft.AspNetCore.Components.Web.HeadContent>().Should().NotBeNull();
    }

    [Fact]
    public void YamlView_folds_top_level_keys_and_expand_all_reopens_them()
    {
        var cut = Render<YamlView>(p => p.Add(c => c.Yaml, Yaml));

        cut.FindAll(".k8s-yaml-key").Select(k => k.TextContent).Should().HaveCount(4);
        cut.Find("pre.k8s-yaml").TextContent.Should().Contain("name: checkout-api");

        cut.FindAll(".k8s-yaml-key").First(k => k.TextContent.Contains("metadata:")).Click();
        cut.Find("pre.k8s-yaml").TextContent.Should().NotContain("name: checkout-api");

        cut.Find(".k8s-yaml-expand").Click();
        cut.Find("pre.k8s-yaml").TextContent.Should().Contain("name: checkout-api");
    }

    [Fact]
    public void YamlView_copy_sends_the_whole_document_to_the_clipboard_helper()
    {
        var module = JSInterop.SetupModule(K8sJs.ModulePath);
        module.SetupVoid("copyText", Yaml);
        var cut = Render<YamlView>(p => p.Add(c => c.Yaml, Yaml));

        cut.Find(".k8s-yaml-copy").Click();

        cut.WaitForAssertion(() => module.VerifyInvoke("copyText").Arguments.Should().Equal(Yaml));
    }

    [Fact]
    public async Task YamlDialog_loads_and_shows_the_yaml()
    {
        var cut = Render<MudDialogProvider>();
        var dialogs = Services.GetRequiredService<IDialogService>();

        await cut.InvokeAsync(() => YamlDialog.ShowAsync(dialogs, "deployment/checkout-api",
            () => Task.FromResult(PluginResult<string?>.Ok(Yaml))));

        cut.WaitForAssertion(() => cut.Find("pre.k8s-yaml").TextContent.Should().Contain("kind: Deployment"));
        cut.Markup.Should().Contain("deployment/checkout-api");
    }

    [Fact]
    public async Task YamlDialog_says_when_the_resource_is_gone()
    {
        var cut = Render<MudDialogProvider>();
        var dialogs = Services.GetRequiredService<IDialogService>();

        await cut.InvokeAsync(() => YamlDialog.ShowAsync(dialogs, "pod/x", () => Task.FromResult(PluginResult<string?>.Ok(null))));

        cut.WaitForAssertion(() => cut.Find(".k8s-yaml-gone").TextContent.Should().Be("It no longer exists."));
    }

    [Fact]
    public async Task YamlDialog_shows_a_load_error()
    {
        var cut = Render<MudDialogProvider>();
        var dialogs = Services.GetRequiredService<IDialogService>();

        await cut.InvokeAsync(() => YamlDialog.ShowAsync(dialogs, "pod/x", () => Task.FromResult(PluginResult<string?>.Fail("Can't reach k.example."))));

        cut.WaitForAssertion(() => cut.Find(".k8s-error-message").TextContent.Should().Be("Can't reach k.example."));
    }
}
