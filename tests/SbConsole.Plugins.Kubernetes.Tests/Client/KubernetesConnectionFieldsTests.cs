using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using MudBlazor.Services;
using SbConsole.Plugins.Kubernetes.Client;

namespace SbConsole.Plugins.Kubernetes.Tests.Client;

public class KubernetesConnectionFieldsTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    public KubernetesConnectionFieldsTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<KubernetesConnectionFields> RenderFields(string? initialSecret, out List<string> emitted)
    {
        var captured = new List<string>();
        emitted = captured;
        Render<MudPopoverProvider>();
        return Render<KubernetesConnectionFields>(parameters => parameters
            .Add(p => p.InitialSecret, initialSecret)
            .Add(p => p.IsProd, true)
            .Add(p => p.SecretChanged, EventCallback.Factory.Create<string>(this, s => captured.Add(s))));
    }

    private static string TwoContexts(string current) => """
        apiVersion: v1
        kind: Config
        current-context: CURRENT
        clusters:
        - name: c1
          cluster:
            server: https://k.example:6443
        contexts:
        - name: dev
          context: {cluster: c1, user: u1}
        - name: prod
          context: {cluster: c1, user: u1}
        users:
        - name: u1
          user:
            token: abc
        """.Replace("CURRENT", current);

    [Fact]
    public void Fills_fields_from_the_initial_secret()
    {
        var yaml = TwoContexts("dev");
        var cut = RenderFields(new KubeconfigSettings(yaml, "prod").ToSecretString(), out var emitted);

        cut.Find(".k8s-conn-kubeconfig textarea").TextContent.Should().Contain("current-context: dev");
        cut.Find(".k8s-conn-context input").GetAttribute("value").Should().Be("prod");
        cut.Find(".k8s-conn-summary").TextContent.Should().Contain("https://k.example:6443").And.Contain("user u1").And.Contain("token");
        cut.FindAll(".k8s-conn-error").Should().BeEmpty();
        emitted.Should().BeEmpty();
    }

    [Fact]
    public void Pasting_a_valid_kubeconfig_emits_a_secret_with_its_current_context()
    {
        var cut = RenderFields(null, out var emitted);

        cut.Find(".k8s-conn-kubeconfig textarea").Input(TwoContexts("prod"));

        cut.WaitForAssertion(() => emitted.Should().NotBeEmpty());
        var s = KubeconfigSettings.Parse(emitted.Last());
        s.Context.Should().Be("prod");
        s.Kubeconfig.Should().Contain("current-context: prod");
    }

    [Fact]
    public void Pasting_an_exec_kubeconfig_shows_the_error_and_emits_nothing()
    {
        var cut = RenderFields(null, out var emitted);
        var exec = TestData.Kubeconfig("exec:\n  apiVersion: client.authentication.k8s.io/v1\n  command: aws");

        cut.Find(".k8s-conn-kubeconfig textarea").Input(exec);

        cut.WaitForAssertion(() => cut.Find(".k8s-conn-error").TextContent.Should().Contain("static credentials"));
        emitted.Should().BeEmpty();
    }

    [Fact]
    public void Choosing_another_context_emits_it()
    {
        var cut = RenderFields(new KubeconfigSettings(TwoContexts("dev"), "dev").ToSecretString(), out var emitted);

        // The popover click path isn't reliably drivable from bUnit; raise the select's ValueChanged directly.
        var select = cut.FindComponent<MudSelect<string>>();
        cut.InvokeAsync(() => select.Instance.ValueChanged.InvokeAsync("prod"));

        cut.WaitForAssertion(() => emitted.Should().NotBeEmpty());
        KubeconfigSettings.Parse(emitted.Last()).Context.Should().Be("prod");
    }

    [Fact]
    public void Shows_tls_not_verified_chip()
    {
        var yaml = TestData.Kubeconfig(skipTls: true);
        var cut = RenderFields(new KubeconfigSettings(yaml, "dev").ToSecretString(), out _);

        cut.Find(".k8s-conn-tls-off").TextContent.Should().Contain("TLS not verified");
    }

    [Fact]
    public void Unparsable_initial_secret_renders_an_empty_form()
    {
        var cut = RenderFields("not json {{", out var emitted);

        cut.Find(".k8s-conn-kubeconfig textarea").TextContent.Should().BeEmpty();
        cut.FindAll(".k8s-conn-error").Should().BeEmpty();
        cut.FindAll(".k8s-conn-summary").Should().BeEmpty();
        emitted.Should().BeEmpty();
    }
}
