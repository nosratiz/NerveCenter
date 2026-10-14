using System.Net;
using FluentAssertions;
using k8s.Autorest;
using SbConsole.Plugins.Kubernetes.Client;

namespace SbConsole.Plugins.Kubernetes.Tests.Operations;

public class NamespaceFanOutTests
{
    private static HttpOperationException Forbidden() => new("forbidden")
    {
        Response = new HttpResponseMessageWrapper(new HttpResponseMessage(HttpStatusCode.Forbidden), ""),
    };

    [Fact]
    public async Task Concurrent_scoped_lists_sharing_one_gate_never_exceed_the_fan_out_cap()
    {
        var namespaces = Enumerable.Range(0, 20).Select(i => $"ns{i}").ToList();
        using var gate = new SemaphoreSlim(KubernetesOperations.MaxNamespaceFanOut);
        var inFlight = 0;
        var max = 0;

        async Task<IList<int>> One(string ns)
        {
            var now = Interlocked.Increment(ref inFlight);
            int seen;
            while (now > (seen = Volatile.Read(ref max)) && Interlocked.CompareExchange(ref max, now, seen) != seen)
            {
            }

            await Task.Delay(10);
            Interlocked.Decrement(ref inFlight);
            return [1];
        }

        var lists = Enumerable.Range(0, 4).Select(_ => KubernetesOperations.ListScopedAsync<int>(
            namespaces, () => Task.FromException<IList<int>>(Forbidden()), One, gate, CancellationToken.None)).ToList();
        var results = await Task.WhenAll(lists);

        results.Should().OnlyContain(r => r.Count == 20);
        max.Should().BeLessThanOrEqualTo(KubernetesOperations.MaxNamespaceFanOut);
        max.Should().BeGreaterThan(1);
    }
}
