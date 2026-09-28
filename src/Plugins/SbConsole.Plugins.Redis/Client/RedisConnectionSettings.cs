using System.Net;
using StackExchange.Redis;

namespace SbConsole.Plugins.Redis.Client;

/// <summary>
/// The plugin's connection secret is "{topology}|{preferReplica}|{configurationOptionsString}".
/// The configuration-options portion is produced and consumed entirely by
/// StackExchange.Redis's own <see cref="ConfigurationOptions.Parse(string)"/> / <c>ToString()</c> —
/// endpoints, TLS, ServiceName and DefaultDatabase all round-trip through the real library, not a
/// hand-rolled parser. The one exception: <c>ConfigurationOptions</c> has no escape syntax for a
/// comma inside a value, because comma is its own field separator (see
/// docs/superpowers/specs/2026-09-28-redis-plugin-design.md §2 and the upstream
/// StackExchange/StackExchange.Redis issues #680 and #883), so a password or user containing a
/// comma cannot survive a raw round trip. To fix only that gap, <c>User</c>/<c>Password</c> are
/// percent-encoded (<see cref="Uri.EscapeDataString"/>/<see cref="Uri.UnescapeDataString"/>)
/// before/after being handed to the library — every other field passes through untouched. The
/// topology/preferReplica prefix carries the two things ConfigurationOptions can't unambiguously
/// express on its own — see the design spec for why.
/// </summary>
public sealed record RedisConnectionSettings(
    RedisTopology Topology,
    IReadOnlyList<string> EndpointHostPorts,
    string? User,
    string? Password,
    bool Ssl,
    string? ServiceName,
    bool PreferReplicaForReads,
    int DefaultDatabase)
{
    public static RedisConnectionSettings Parse(string secret)
    {
        var parts = secret.Split('|', 3);
        if (parts.Length != 3)
        {
            throw new FormatException(
                "Redis connection secret must be '{topology}|{preferReplica}|{configurationOptions}'.");
        }

        var topology = Enum.Parse<RedisTopology>(parts[0], ignoreCase: true);
        var preferReplica = parts[1] == "1";
        // Genuinely reuse StackExchange.Redis's own parser for everything (endpoints, TLS,
        // ServiceName, DefaultDatabase) — the ONLY hand-written layer is decoding User/Password,
        // because ConfigurationOptions has no escape syntax for a comma inside a value (comma is
        // its field separator; see the design spec §2 for the upstream issue links). Every other
        // field round-trips through the library exactly as it would for any other caller.
        var options = ConfigurationOptions.Parse(parts[2]);

        return new RedisConnectionSettings(
            topology,
            options.EndPoints.Select(EndpointToHostPort).ToList(),
            options.User is null ? null : Uri.UnescapeDataString(options.User),
            options.Password is null ? null : Uri.UnescapeDataString(options.Password),
            options.Ssl,
            options.ServiceName,
            preferReplica,
            options.DefaultDatabase ?? 0);
    }

    public string ToSecretString()
    {
        var options = ToConfigurationOptionsCore(encodeCredentials: true);
        var topologyToken = Topology.ToString().ToLowerInvariant();
        var preferReplicaToken = PreferReplicaForReads ? "1" : "0";
        return $"{topologyToken}|{preferReplicaToken}|{options}";
    }

    /// <summary>The real, connectable options — raw (unescaped) User/Password.</summary>
    public ConfigurationOptions ToConfigurationOptions() => ToConfigurationOptionsCore(encodeCredentials: false);

    private ConfigurationOptions ToConfigurationOptionsCore(bool encodeCredentials)
    {
        var options = new ConfigurationOptions
        {
            User = EncodeIfNeeded(User, encodeCredentials),
            Password = EncodeIfNeeded(Password, encodeCredentials),
            Ssl = Ssl,
            // ServiceName only applies when the topology is actually Sentinel: setting it on
            // ConfigurationOptions puts StackExchange.Redis into Sentinel mode automatically
            // (see RedisConnectionFactory), so a leftover value from a user who switched the
            // topology control away from Sentinel must never leak into the built options —
            // otherwise a plain Standalone connection would silently try to connect as Sentinel
            // and fail confusingly. The record's own ServiceName field is left untouched so the
            // connection form can still show it if the user switches back to Sentinel.
            ServiceName = Topology == RedisTopology.Sentinel ? ServiceName : null,
            AllowAdmin = true, // SbConsole is an operator tool and needs MEMORY/SLOWLOG/CLIENT — the
                                // Redis ACL, not this client-side flag, is the real gate.
            DefaultDatabase = Topology == RedisTopology.Cluster ? 0 : DefaultDatabase,
        };

        foreach (var hostPort in EndpointHostPorts)
        {
            options.EndPoints.Add(hostPort);
        }

        return options;
    }

    private static string? EncodeIfNeeded(string? value, bool encode) =>
        value is null || !encode ? value : Uri.EscapeDataString(value);

    /// <summary>
    /// EndPoint.ToString() renders "AddressFamily/host:port" (e.g. "Unspecified/redis-01:6380"),
    /// not the plain "host:port" this plugin's secret format and UI need — hence this helper
    /// instead of calling ToString() on the raw EndPoint.
    /// </summary>
    private static string EndpointToHostPort(EndPoint endpoint) => endpoint switch
    {
        DnsEndPoint dns => $"{dns.Host}:{dns.Port}",
        IPEndPoint ip => $"{ip.Address}:{ip.Port}",
        _ => endpoint.ToString()!,
    };
}
