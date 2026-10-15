using FluentAssertions;
using SbConsole.Plugins.Kubernetes.Client;

namespace SbConsole.Plugins.Kubernetes.Tests.Client;

public class KubernetesClientFactoryTests
{
    private static string Secret(string token) =>
        new KubeconfigSettings(TestData.Kubeconfig($"token: {token}"), "dev").ToSecretString();

    [Fact]
    public void Same_secret_returns_the_same_client()
    {
        using var factory = new KubernetesClientFactory();

        factory.Get(Secret("a")).Should().BeSameAs(factory.Get(Secret("a")));
    }

    [Fact]
    public void Different_secrets_return_different_clients()
    {
        using var factory = new KubernetesClientFactory();

        factory.Get(Secret("a")).Should().NotBeSameAs(factory.Get(Secret("b")));
    }

    [Fact]
    public void Bad_secret_throws_FormatException()
    {
        using var factory = new KubernetesClientFactory();

        factory.Invoking(f => f.Get("not json")).Should().Throw<FormatException>();
    }

    [Fact]
    public void Evicts_the_least_recently_used_client_beyond_the_cap()
    {
        using var factory = new KubernetesClientFactory();
        var first = factory.Get(Secret("t0"));
        var second = factory.Get(Secret("t1"));
        for (var i = 2; i < KubernetesClientFactory.MaxClients; i++)
        {
            factory.Get(Secret($"t{i}"));
        }

        factory.Get(Secret("t0")).Should().BeSameAs(first); // touch t0 so t1 is now the oldest
        factory.Get(Secret("overflow"));

        factory.Get(Secret("t0")).Should().BeSameAs(first);
        factory.Get(Secret("t1")).Should().NotBeSameAs(second);
    }

    [Fact]
    public void A_kubeconfig_with_a_case_variant_exec_user_is_rejected_before_any_client_is_built()
    {
        var yaml = TestData.Kubeconfig().Replace("user: u1", "user: Admin").Replace("- name: u1", "- name: Admin") + """

            - name: admin
              user:
                exec: { apiVersion: client.authentication.k8s.io/v1, command: /usr/bin/touch, args: ["/tmp/pwned"] }
            """;
        using var factory = new KubernetesClientFactory();

        factory.Invoking(f => f.Get(new KubeconfigSettings(yaml, "dev").ToSecretString()))
            .Should().Throw<FormatException>().WithMessage("*unique ignoring case*");
    }

    [Fact]
    public void Trim_keeps_only_the_exact_context_cluster_and_user_so_a_case_variant_exec_user_can_never_be_picked()
    {
        // Built without the validator, to prove the trimmed config alone is safe.
        var yaml = """
            apiVersion: v1
            kind: Config
            clusters:
            - name: C1
              cluster: { server: "https://evil.example" }
            - name: c1
              cluster: { server: "https://k.example:6443" }
            contexts:
            - name: Dev
              context: { cluster: C1, user: admin }
            - name: dev
              context: { cluster: c1, user: Admin }
            users:
            - name: admin
              user:
                exec: { apiVersion: client.authentication.k8s.io/v1, command: /usr/bin/touch, args: ["/tmp/pwned"] }
            - name: Admin
              user: { token: abc }
            """;
        var parsed = k8s.KubernetesClientConfiguration.LoadKubeConfig(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(yaml)));

        var trimmed = KubernetesClientFactory.Trim(parsed, "dev");

        trimmed.CurrentContext.Should().Be("dev");
        trimmed.Contexts.Select(c => c.Name).Should().Equal("dev");
        trimmed.Clusters.Select(c => c.Name).Should().Equal("c1");
        trimmed.Users.Select(u => u.Name).Should().Equal("Admin");
        trimmed.Users.Single().Should().NotBeSameAs(parsed.Users.Last(), "the trimmed config is a deep copy");
        var built = k8s.KubernetesClientConfiguration.BuildConfigFromConfigObject(trimmed, trimmed.CurrentContext);
        built.AccessToken.Should().Be("abc");
        built.Host.Should().StartWith("https://k.example:6443");
        built.TokenProvider.Should().BeNull();
    }

    [Fact]
    public void BuildConfiguration_uses_the_validated_token_user()
    {
        var config = KubernetesClientFactory.BuildConfiguration(KubeconfigSettings.Parse(Secret("tok")));

        config.AccessToken.Should().Be("tok");
        config.Host.Should().StartWith("https://k.example:6443");
    }
}
