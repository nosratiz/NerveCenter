using System.Net;
using System.Text;
using StackExchange.Redis;

namespace SbConsole.Plugins.Redis.Client;

/// <summary>
/// The plugin's connection secret is "{topology}|{preferReplica}|{configurationOptionsString}" —
/// StackExchange.Redis's own ConfigurationOptions format already handles multi-endpoint lists,
/// TLS, ACL user/password and comma/quote escaping in passwords, so it's reused verbatim rather
/// than inventing a parser (docs/superpowers/specs/2026-09-28-redis-plugin-design.md §2). The
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

        // Manually parse the configuration string to handle passwords with special characters
        var configParts = ParseConfigurationString(parts[2]);

        var endpoints = new List<string>();
        string? user = null;
        string? password = null;
        bool ssl = false;
        string? serviceName = null;
        int defaultDatabase = 0;

        foreach (var (key, value) in configParts)
        {
            switch (key.ToLowerInvariant())
            {
                case "user":
                    user = value;
                    break;
                case "password":
                    password = value;
                    break;
                case "ssl":
                    ssl = value.Equals("true", StringComparison.OrdinalIgnoreCase);
                    break;
                case "servicename":
                    serviceName = value;
                    break;
                case "defaultdatabase":
                    if (int.TryParse(value, out var db))
                        defaultDatabase = db;
                    break;
                default:
                    // If not a recognized option, treat as an endpoint
                    if (!key.Contains('='))
                    {
                        endpoints.Add(key);
                    }
                    break;
            }
        }

        return new RedisConnectionSettings(
            topology,
            endpoints,
            user,
            password,
            ssl,
            serviceName,
            preferReplica,
            defaultDatabase);
    }

    /// <summary>
    /// Manually parse the configuration string, respecting quoted values for passwords with special characters.
    /// </summary>
    private static List<(string Key, string Value)> ParseConfigurationString(string config)
    {
        var result = new List<(string Key, string Value)>();
        var parts = SplitConfigurationString(config);

        foreach (var part in parts)
        {
            var eqIndex = part.IndexOf('=');
            if (eqIndex > 0)
            {
                var key = part.Substring(0, eqIndex);
                var value = part.Substring(eqIndex + 1);

                // Remove surrounding quotes if present
                if (value.StartsWith('"') && value.EndsWith('"') && value.Length > 1)
                {
                    value = value.Substring(1, value.Length - 2);
                }

                result.Add((key, value));
            }
            else if (!string.IsNullOrWhiteSpace(part))
            {
                // Treat as endpoint (no equals sign)
                result.Add((part, ""));
            }
        }

        return result;
    }

    /// <summary>
    /// Split configuration string on commas, but respect quoted values.
    /// </summary>
    private static List<string> SplitConfigurationString(string config)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;

        foreach (var c in config)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                current.Append(c);
            }
            else if (c == ',' && !inQuotes)
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            parts.Add(current.ToString());
        }

        return parts;
    }

    public string ToSecretString()
    {
        var parts = new List<string>();

        // Add endpoints
        foreach (var hostPort in EndpointHostPorts)
        {
            parts.Add(hostPort);
        }

        // Add user if set
        if (!string.IsNullOrEmpty(User))
        {
            parts.Add($"user={User}");
        }

        // Add password if set, with quoting if it contains special characters
        if (!string.IsNullOrEmpty(Password))
        {
            if (Password.Any(c => c == ',' || c == '='))
            {
                parts.Add($"password=\"{Password}\"");
            }
            else
            {
                parts.Add($"password={Password}");
            }
        }

        // Add SSL
        parts.Add($"ssl={Ssl.ToString().ToLowerInvariant()}");

        // Add ServiceName if set
        if (!string.IsNullOrEmpty(ServiceName))
        {
            parts.Add($"serviceName={ServiceName}");
        }

        // Add DefaultDatabase if not 0 or for non-cluster topologies
        if (DefaultDatabase != 0 || Topology != RedisTopology.Cluster)
        {
            parts.Add($"defaultDatabase={DefaultDatabase}");
        }

        var configString = string.Join(",", parts);

        var topologyToken = Topology.ToString().ToLowerInvariant();
        var preferReplicaToken = PreferReplicaForReads ? "1" : "0";
        return $"{topologyToken}|{preferReplicaToken}|{configString}";
    }

    public ConfigurationOptions ToConfigurationOptions()
    {
        var options = new ConfigurationOptions
        {
            User = User,
            Password = Password,
            Ssl = Ssl,
            ServiceName = ServiceName,
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
