// Allowed exception to the "only KubernetesOperations*/ClientFactory/K8sMappers touch k8s.*" rule: this file must catch k8s exception types.
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.RegularExpressions;
using k8s.Autorest;
using k8s.Exceptions;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes.Client;

/// <summary>The only path raw Kubernetes client exception text takes to the UI or audit rows. Never includes request content.</summary>
public static partial class FriendlyKubernetesError
{
    /// <summary>A 409: the object's resourceVersion moved on since it was read (optimistic concurrency).</summary>
    public const string Conflict = "It changed since you loaded it — reload and try again.";

    public static string From(Exception ex) => Describe(ex).Message;

    public static K8sErrorInfo Describe(Exception ex)
    {
        switch (ex)
        {
            case HttpOperationException http:
                return DescribeHttp(http);
            case HttpRequestException { InnerException: AuthenticationException }:
                return Unreachable("The server's certificate isn't trusted — add its CA to the kubeconfig.");
            case HttpRequestException { InnerException: SocketException socket }:
                return socket.SocketErrorCode switch
                {
                    SocketError.ConnectionRefused => Unreachable("Can't reach the cluster — the connection was refused."),
                    SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData =>
                        Unreachable("Can't reach the cluster — the host name didn't resolve."),
                    _ => Unreachable("Can't reach the cluster."),
                };
            case HttpRequestException:
                return Unreachable("Can't reach the cluster.");
            case TaskCanceledException or TimeoutException:
                return Unreachable("Can't reach the cluster — it didn't answer in time.");
            case FormatException format:
                return Plain(FriendlyError.Truncate("The saved connection settings are invalid: " + format.Message));
            case KubeConfigException kube:
                return Plain(FriendlyError.Truncate("The saved kubeconfig can't be used: " + kube.Message));
            default:
                return Plain(FriendlyError.From(ex));
        }
    }

    private static K8sErrorInfo Plain(string message) => new(message, null, null, false);

    private static K8sErrorInfo Unreachable(string message) => new(message, null, null, true);

    private static K8sErrorInfo DescribeHttp(HttpOperationException ex)
    {
        var code = (int?)ex.Response?.StatusCode;
        if (code is null)
        {
            return Plain(FriendlyError.From(ex));
        }

        var status = StatusMessage(ex);
        var message = code.Value switch
        {
            401 => "The cluster rejected the credentials, or they have expired.",
            403 => Forbidden(status),
            404 => "It no longer exists — refresh.",
            409 => Conflict,
            422 => "The cluster rejected the change: " + (status is null ? "invalid." : FriendlyError.Truncate(status).TrimEnd('.') + "."),
            429 => "Blocked by a PodDisruptionBudget — try again later.",
            >= 500 => $"The API server returned an error ({code}).",
            _ => $"The API server rejected the request ({code}).",
        };
        return new K8sErrorInfo(message, code, null, false);
    }

    private static string Forbidden(string? status)
    {
        if (status is not null)
        {
            var m = ForbiddenPattern().Match(status);
            if (m.Success)
            {
                var ns = NamespacePattern().Match(status);
                return $"Your account can't {m.Groups[1].Value} {m.Groups[2].Value}{(ns.Success ? " in " + ns.Groups[1].Value : "")}.";
            }
        }

        return "Your account isn't allowed to do that.";
    }

    private static string? StatusMessage(HttpOperationException ex)
    {
        try
        {
            var content = ex.Response?.Content;
            if (string.IsNullOrWhiteSpace(content))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(content);
            return doc.RootElement.TryGetProperty("message", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    [GeneratedRegex("""cannot (\w+) (?:resource )?"([^"]+)""")]
    private static partial Regex ForbiddenPattern();

    [GeneratedRegex("""in the namespace "([^"]+)""")]
    private static partial Regex NamespacePattern();
}
