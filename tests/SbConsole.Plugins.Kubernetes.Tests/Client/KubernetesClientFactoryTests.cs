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
}
