using System.Globalization;
using System.Text;

namespace SbConsole.Plugins.RabbitMq.Client;

/// <summary>
/// One connection's resolved settings, with every §2 default applied. Every operation takes the
/// raw secret and builds this itself (no instance pre-configured for one connection). Missing
/// required keys and unparseable values throw <see cref="InvalidOperationException"/> with a
/// short message naming the key, which FriendlyRabbitError passes through unchanged.
/// </summary>
public sealed record RabbitConnectionSettings
{
    public const int DefaultAmqpPort = 5672;
    public const int DefaultAmqpTlsPort = 5671;
    public const int DefaultManagementPort = 15672;
    public const int DefaultManagementTlsPort = 15671;

    private RabbitConnectionSettings(
        string host, int amqpPort, Uri managementBaseUri, string vhost,
        string username, string password, bool tls, bool verifyCert)
    {
        Host = host;
        AmqpPort = amqpPort;
        ManagementBaseUri = managementBaseUri;
        Vhost = vhost;
        Username = username;
        Password = password;
        Tls = tls;
        VerifyCert = verifyCert;
    }

    public string Host { get; }

    public int AmqpPort { get; }

    /// <summary>
    /// Management API root, always ending in "/" so relative paths ("api/overview") resolve under
    /// a path-prefixed deployment (e.g. "https://mgmt.example/rabbitmq/").
    /// </summary>
    public Uri ManagementBaseUri { get; }

    public string Vhost { get; }

    public string Username { get; }

    public string Password { get; }

    public bool Tls { get; }

    /// <summary>Only meaningful with TLS; applies to both AMQP and management HTTPS.</summary>
    public bool VerifyCert { get; }

    public static RabbitConnectionSettings From(string secret)
    {
        var fields = RabbitConfigParser.Parse(secret);

        var host = Required(fields, "host");
        var username = Required(fields, "username");
        var password = Required(fields, "password");
        var tls = Bool(fields, "tls", defaultValue: false);
        var verifyCert = Bool(fields, "verifyCert", defaultValue: true);

        var amqpPort = tls ? DefaultAmqpTlsPort : DefaultAmqpPort;
        if (fields.TryGetValue("amqpPort", out var portText) && portText.Length > 0)
        {
            if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out amqpPort)
                || amqpPort is < 1 or > 65535)
            {
                throw new InvalidOperationException("Connection has an invalid 'amqpPort' (expected 1–65535).");
            }
        }

        var managementBaseUri = fields.TryGetValue("managementUrl", out var urlText) && urlText.Length > 0
            ? ParseManagementUrl(urlText)
            : new UriBuilder(
                tls ? Uri.UriSchemeHttps : Uri.UriSchemeHttp,
                host,
                tls ? DefaultManagementTlsPort : DefaultManagementPort).Uri;

        var vhost = fields.TryGetValue("vhost", out var vhostText) && vhostText.Length > 0 ? vhostText : "/";

        return new RabbitConnectionSettings(
            host, amqpPort, WithTrailingSlash(managementBaseUri), vhost, username, password, tls, verifyCert);
    }

    // Records print every property by default; the password must never reach a log line.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(CultureInfo.InvariantCulture,
            $"Host = {Host}, AmqpPort = {AmqpPort}, ManagementBaseUri = {ManagementBaseUri}, Vhost = {Vhost}, ");
        builder.Append(CultureInfo.InvariantCulture,
            $"Username = {Username}, Password = ***, Tls = {Tls}, VerifyCert = {VerifyCert}");
        return true;
    }

    private static string Required(Dictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Connection is missing '{key}'.");

    private static bool Bool(Dictionary<string, string> fields, string key, bool defaultValue)
    {
        if (!fields.TryGetValue(key, out var text) || text.Length == 0)
        {
            return defaultValue;
        }

        return bool.TryParse(text, out var value)
            ? value
            : throw new InvalidOperationException($"Connection has an invalid '{key}' (expected true or false).");
    }

    private static Uri ParseManagementUrl(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri
            : throw new InvalidOperationException("Connection has an invalid 'managementUrl' (expected an http or https URL).");

    private static Uri WithTrailingSlash(Uri uri) =>
        uri.AbsolutePath.EndsWith('/') ? uri : new UriBuilder(uri) { Path = uri.AbsolutePath + "/" }.Uri;
}
