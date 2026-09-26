using System.Net;
using System.Text;
using SbConsole.Plugins.RabbitMq.Client;

namespace SbConsole.Plugins.RabbitMq.Tests.Client;

/// <summary>One request the fake handler saw, with the body already read (the request is disposed after).</summary>
internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Authorization, string? Accept, string? Body, string? ContentType);

/// <summary>
/// Canned management API: responses keyed by "METHOD /path?query" exactly as the escaped request
/// URI carries them. Unknown routes answer 404 with the broker's real error body.
/// </summary>
internal sealed class FakeManagementHandler : HttpMessageHandler
{
    private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _routes = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public List<RecordedRequest> Requests { get; } = [];

    public FakeManagementHandler Respond(string method, string pathAndQuery, string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes[$"{method} {pathAndQuery}"] = (status, body);
        return this;
    }

    public FakeManagementHandler RespondFixture(string pathAndQuery, string fixture) =>
        Respond("GET", pathAndQuery, Fixtures.Read(fixture));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (_gate)
        {
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!,
                request.Headers.Authorization?.ToString(),
                request.Headers.Accept.ToString(),
                body,
                request.Content?.Headers.ContentType?.MediaType));
        }

        var key = $"{request.Method.Method} {request.RequestUri!.PathAndQuery}";
        var (status, responseBody) = _routes.TryGetValue(key, out var route)
            ? route
            : (HttpStatusCode.NotFound, """{"error":"Object Not Found","reason":"Not Found"}""");

        return new HttpResponseMessage(status)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
        };
    }
}

internal static class Fixtures
{
    public static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Client", "Fixtures", name));
}

internal static class TestSettings
{
    // Password carries ':' and non-ASCII to prove the Basic header encodes UTF-8 user:pass verbatim.
    public static RabbitConnectionSettings Orders { get; } = RabbitConnectionSettings.From(
        "host=localhost;username=sbconsole;password=s%3Acr%C3%A9t;vhost=%2Forders");

    public static (ManagementApiClient Client, FakeManagementHandler Handler) Create()
    {
        var handler = new FakeManagementHandler();
        return (new ManagementApiClient(_ => handler), handler);
    }
}
