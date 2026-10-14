using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SbConsole.Sdk;
using SbConsole.Plugins.Kubernetes.Client;

namespace SbConsole.Plugins.Kubernetes.Tests;

public class KubernetesPluginTests
{
    [Fact]
    public void Identity_is_kubernetes()
    {
        var plugin = new KubernetesPlugin();

        plugin.Id.Should().Be("kubernetes");
        plugin.ConnectionKind.Should().Be("kubernetes");
        plugin.DisplayName.Should().Be("Kubernetes");
        plugin.ConnectionKindDisplayName.Should().Be("Kubernetes");
        plugin.Monogram.Should().Be("K8");
        plugin.Version.Should().Be("0.1.0");
        plugin.Contribution.PageCount.Should().Be(7);
        plugin.Contribution.ActionCount.Should().Be(9);
    }

    [Fact]
    public void Nav_items_are_in_order()
    {
        var nav = new KubernetesPlugin().NavItems;

        nav.Select(n => n.Title).Should().Equal("Overview", "Workloads", "Nodes", "Config", "Events");
        nav.Select(n => n.Href).Should().Equal(
            "/p/kubernetes/overview", "/p/kubernetes/workloads", "/p/kubernetes/nodes",
            "/p/kubernetes/config", "/p/kubernetes/events");
    }

    [Fact]
    public void DeploymentHref_escapes_segments()
    {
        KubernetesPlugin.DeploymentHref("my ns", "a/b").Should().Be("/p/kubernetes/workloads/deployments/my%20ns/a%2Fb");
        KubernetesPlugin.PodHref("ns", "p 1").Should().Be("/p/kubernetes/pods/ns/p%201");
    }

    [Fact]
    public void Connection_summary_has_server_context_tls_and_never_the_token()
    {
        var secret = new KubeconfigSettings(TestData.Kubeconfig("token: s3cr3t-token-text"), "dev").ToSecretString();

        var summary = new KubernetesPlugin().GetConnectionSummary(secret);

        summary.Should().Equal(new Dictionary<string, string>
        {
            ["Server"] = "k.example", ["Context"] = "dev", ["TLS"] = "verified",
        });
        string.Concat(summary.Values).Should().NotContain("s3cr3t").And.NotContain("u1");
    }

    [Fact]
    public void Connection_summary_reports_unverified_tls()
    {
        var secret = new KubeconfigSettings(TestData.Kubeconfig(skipTls: true), "dev").ToSecretString();

        new KubernetesPlugin().GetConnectionSummary(secret)["TLS"].Should().Be("not verified");
    }

    [Fact]
    public void Connection_summary_is_empty_on_garbage()
    {
        new KubernetesPlugin().GetConnectionSummary("not a secret ;;; ").Should().BeEmpty();
    }

    [Fact]
    public async Task TestConnection_fails_closed_before_initialize()
    {
        var result = await new KubernetesPlugin().TestConnectionAsync("{}");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Kubernetes plugin is not initialized.");
    }

    [Fact]
    public void ConfigureServices_registers_the_operations_and_the_factory_as_singletons()
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(Substitute.For<IConnectionProvider>()); // host-provided; handlers depend on it
        services.AddScoped(_ => Substitute.For<IAuditScope>()); // host-provided; command handlers depend on it
        services.AddKeyedScoped<IPluginStore>(KubernetesPlugin.PluginId, (_, _) => new InMemoryPluginStore()); // host-provided, keyed
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        new KubernetesPlugin().ConfigureServices(services);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        provider.GetRequiredService<IKubernetesOperations>().Should().BeOfType<KubernetesOperations>()
            .And.BeSameAs(provider.GetRequiredService<IKubernetesOperations>());
        provider.GetRequiredService<KubernetesClientFactory>().Should().BeSameAs(provider.GetRequiredService<KubernetesClientFactory>());
        var plugin = new KubernetesPlugin();
        plugin.Invoking(p => p.Initialize(provider)).Should().NotThrow();
    }

    [Fact]
    public async Task TestConnection_delegates_to_the_operations()
    {
        var ops = Substitute.For<IKubernetesOperations>();
        ops.TestConnectionAsync("s", Arg.Any<CancellationToken>()).Returns(new ConnectionTestResult(true, null, "v1 · as me"));
        var plugin = new KubernetesPlugin();
        plugin.InitializeForTesting(ops);

        var result = await plugin.TestConnectionAsync("s");

        result.Identity.Should().Be("v1 · as me");
    }
}
