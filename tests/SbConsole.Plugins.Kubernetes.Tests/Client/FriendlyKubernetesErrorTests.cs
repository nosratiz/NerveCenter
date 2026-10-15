using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using FluentAssertions;
using k8s.Autorest;
using k8s.Exceptions;
using SbConsole.Plugins.Kubernetes.Client;

namespace SbConsole.Plugins.Kubernetes.Tests.Client;

public class FriendlyKubernetesErrorTests
{
    private static HttpOperationException Http(HttpStatusCode code, string? message = null, bool withRequest = false)
    {
        var json = message is null ? "" : "{\"kind\":\"Status\",\"message\":" + System.Text.Json.JsonSerializer.Serialize(message) + "}";
        var ex = new HttpOperationException("raw https://secret-host/api bearer abc123")
        {
            Response = new HttpResponseMessageWrapper(new HttpResponseMessage(code), json),
        };
        if (withRequest)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "https://secret-host/api/v1/pods");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "abc123");
            ex.Request = new HttpRequestMessageWrapper(req, "");
        }

        return ex;
    }

    [Fact]
    public void A_401_says_the_credentials_were_rejected()
    {
        var info = FriendlyKubernetesError.Describe(Http(HttpStatusCode.Unauthorized));
        info.Message.Should().Be("The cluster rejected the credentials, or they have expired.");
        info.StatusCode.Should().Be(401);
        info.IsUnreachable.Should().BeFalse();
    }

    [Fact]
    public void A_403_names_the_verb_resource_and_namespace()
    {
        var msg = FriendlyKubernetesError.From(Http(HttpStatusCode.Forbidden,
            "User \"x\" cannot patch resource \"deployments/scale\" in API group \"apps\" in the namespace \"checkout\""));
        msg.Should().Be("Your account can't patch deployments/scale in checkout.");
    }

    [Fact]
    public void A_cluster_scoped_403_has_no_namespace()
    {
        var msg = FriendlyKubernetesError.From(Http(HttpStatusCode.Forbidden,
            "nodes \"n1\" is forbidden: User \"x\" cannot patch resource \"nodes\" in API group \"\" at the cluster scope"));
        msg.Should().Be("Your account can't patch nodes.");
    }

    [Fact]
    public void An_unparsable_403_falls_back()
    {
        FriendlyKubernetesError.From(Http(HttpStatusCode.Forbidden, "nope"))
            .Should().Be("Your account isn't allowed to do that.");
        FriendlyKubernetesError.From(Http(HttpStatusCode.Forbidden))
            .Should().Be("Your account isn't allowed to do that.");
    }

    [Fact]
    public void Other_status_codes_map_to_their_messages()
    {
        FriendlyKubernetesError.From(Http(HttpStatusCode.NotFound)).Should().Be("It no longer exists — refresh.");
        FriendlyKubernetesError.From(Http(HttpStatusCode.Conflict)).Should().Be("It changed since you loaded it — reload and try again.");
        FriendlyKubernetesError.From(Http(HttpStatusCode.UnprocessableEntity, "spec.replicas: Invalid value"))
            .Should().Be("The cluster rejected the change: spec.replicas: Invalid value.");
        FriendlyKubernetesError.From(Http((HttpStatusCode)429)).Should().Be("Blocked by a PodDisruptionBudget — try again later.");
        var info = FriendlyKubernetesError.Describe(Http(HttpStatusCode.BadGateway));
        info.Message.Should().Be("The API server returned an error (502).");
        info.StatusCode.Should().Be(502);
    }

    [Fact]
    public void Messages_never_include_the_request_url_or_token()
    {
        foreach (var code in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.NotFound, HttpStatusCode.InternalServerError })
        {
            var msg = FriendlyKubernetesError.From(Http(code, withRequest: true));
            msg.Should().NotContain("secret-host").And.NotContain("abc123").And.NotContain("Bearer");
        }
    }

    [Fact]
    public void An_untrusted_certificate_is_reported_as_unreachable()
    {
        var info = FriendlyKubernetesError.Describe(new HttpRequestException("x", new AuthenticationException("bad")));
        info.Message.Should().Be("The server's certificate isn't trusted — add its CA to the kubeconfig.");
        info.IsUnreachable.Should().BeTrue();
    }

    [Theory]
    [InlineData(SocketError.ConnectionRefused, "Can't reach the cluster — the connection was refused.")]
    [InlineData(SocketError.HostNotFound, "Can't reach the cluster — the host name didn't resolve.")]
    [InlineData(SocketError.TryAgain, "Can't reach the cluster — the host name didn't resolve.")]
    [InlineData(SocketError.NoData, "Can't reach the cluster — the host name didn't resolve.")]
    [InlineData(SocketError.NetworkUnreachable, "Can't reach the cluster.")]
    public void Socket_failures_are_described(SocketError error, string expected)
    {
        var info = FriendlyKubernetesError.Describe(new HttpRequestException("x", new SocketException((int)error)));
        info.Message.Should().Be(expected);
        info.IsUnreachable.Should().BeTrue();
    }

    [Fact]
    public void Timeouts_are_described()
    {
        FriendlyKubernetesError.From(new TaskCanceledException()).Should().Be("Can't reach the cluster — it didn't answer in time.");
        FriendlyKubernetesError.From(new TimeoutException()).Should().Be("Can't reach the cluster — it didn't answer in time.");
    }

    [Fact]
    public void Format_and_kubeconfig_errors_are_prefixed()
    {
        FriendlyKubernetesError.From(new FormatException("bad value")).Should().Be("The saved connection settings are invalid: bad value");
        FriendlyKubernetesError.From(new KubeConfigException("no context")).Should().Be("The saved kubeconfig can't be used: no context");
    }

    [Fact]
    public void Anything_else_goes_through_FriendlyError()
    {
        FriendlyKubernetesError.From(new InvalidOperationException("boom")).Should().Be("boom");
    }
}
