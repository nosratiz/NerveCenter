using System.Runtime.CompilerServices;
using System.Text;
using k8s;
using k8s.Autorest;
using Microsoft.Extensions.Logging;

namespace SbConsole.Plugins.Kubernetes.Client;

public sealed partial class KubernetesOperations
{
    /// <summary>Lines per container in "Download logs (all containers)".</summary>
    public const int DownloadTailLines = 10_000;

    /// <summary>
    /// One container's log as lines (at most <see cref="K8sLogRequest.TailLines"/>, newest last). A Previous request for a
    /// container that never restarted is answered 400 "previous terminated container … not found" — that is "no previous
    /// instance", so it returns an empty list rather than failing.
    /// </summary>
    public async Task<IReadOnlyList<string>> ReadLogsAsync(string secret, string ns, string pod, K8sLogRequest request, CancellationToken ct = default)
    {
        Stream stream;
        try
        {
            stream = await Client(secret).CoreV1.ReadNamespacedPodLogAsync(pod, ns, container: request.Container, follow: false,
                previous: request.Previous, tailLines: request.TailLines, timestamps: request.Timestamps, cancellationToken: ct);
        }
        catch (HttpOperationException ex) when (request.Previous && IsNoPreviousInstance(ex))
        {
            return [];
        }

        await using (stream)
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var lines = new Queue<string>();
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                lines.Enqueue(line);
                if (lines.Count > request.TailLines)
                {
                    lines.Dequeue();
                }
            }

            return lines.ToList();
        }
    }

    /// <summary>
    /// Streams the current instance's log: the last <see cref="K8sLogRequest.TailLines"/> lines, then new ones as they
    /// are written, until the container stops or <paramref name="ct"/> is cancelled (which throws). <see cref="K8sLogRequest.Previous"/> is ignored.
    /// </summary>
    public async IAsyncEnumerable<string> FollowLogsAsync(string secret, string ns, string pod, K8sLogRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var stream = await Client(secret).CoreV1.ReadNamespacedPodLogAsync(pod, ns, container: request.Container, follow: true,
            previous: false, tailLines: request.TailLines, timestamps: request.Timestamps, cancellationToken: ct);
        await using (stream)
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (true)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null)
                {
                    yield break;
                }

                yield return line;
            }
        }
    }

    /// <summary>
    /// Every container's (init first, then app) current log, each under a <c>==&gt; {container} &lt;==</c> header and capped at
    /// <see cref="DownloadTailLines"/> lines, as UTF-8. A container whose log can't be read gets a one-line friendly reason instead.
    /// </summary>
    public async Task<byte[]> DownloadAllLogsAsync(string secret, string ns, string pod, CancellationToken ct = default)
    {
        var spec = (await Client(secret).CoreV1.ReadNamespacedPodAsync(pod, ns, cancellationToken: ct)).Spec;
        var containers = (spec?.InitContainers ?? []).Concat(spec?.Containers ?? []).Select(c => c.Name).ToList();

        var text = new StringBuilder();
        foreach (var container in containers)
        {
            text.Append("==> ").Append(container).Append(" <==\n");
            try
            {
                foreach (var line in await ReadLogsAsync(secret, ns, pod, new K8sLogRequest(container, false, DownloadTailLines, false), ct))
                {
                    text.Append(line).Append('\n');
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Reading the log of {Namespace}/{Pod} container {Container} for download failed", ns, pod, container);
                text.Append("(logs unavailable: ").Append(FriendlyKubernetesError.From(ex)).Append(")\n");
            }

            text.Append('\n');
        }

        return Encoding.UTF8.GetBytes(text.ToString());
    }

    private static bool IsNoPreviousInstance(HttpOperationException ex) =>
        (int?)ex.Response?.StatusCode == 400
        && ex.Response?.Content?.Contains("previous terminated container", StringComparison.OrdinalIgnoreCase) == true;
}
