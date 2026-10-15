using System.Globalization;
using System.Text.RegularExpressions;
using SbConsole.Plugins.Kubernetes.Client;

namespace SbConsole.Plugins.Kubernetes.Architecture;

/// <summary>
/// Turns workloads, Services and Ingresses into the dependency map (mockup 4a). Edges come from two detectors: env values
/// that name a Service (DNS name, host:port, URL or ADO-style connection string) or an outside http(s) host, and Ingress
/// backends. The overlay then hides dismissed edges and adds hand-made ones. Pure: no I/O, no clock.
/// Env values are read only to extract a host and port; nothing derived from a value other than those leaves this class.
/// </summary>
public static partial class ArchitectureBuilder
{
    public sealed record Endpoint(string Host, int? Port, string? Scheme);

    private static readonly string[] HostKeys = ["host", "server", "data source", "address", "addr", "hostname"];
    private static readonly string[] HttpSchemes = ["http", "https", "ws", "wss", "grpc", "grpcs"];

    /// <summary>Protocol name, edge kind and default port per scheme alias.</summary>
    private static readonly Dictionary<string, (string Name, ArchEdgeKind Kind)> Protocols = new(StringComparer.OrdinalIgnoreCase)
    {
        ["redis"] = ("redis", ArchEdgeKind.Store), ["rediss"] = ("redis", ArchEdgeKind.Store),
        ["postgres"] = ("postgres", ArchEdgeKind.Store), ["postgresql"] = ("postgres", ArchEdgeKind.Store),
        ["mysql"] = ("mysql", ArchEdgeKind.Store), ["mariadb"] = ("mysql", ArchEdgeKind.Store),
        ["mongodb"] = ("mongodb", ArchEdgeKind.Store), ["mongodb+srv"] = ("mongodb", ArchEdgeKind.Store),
        ["memcached"] = ("memcached", ArchEdgeKind.Store),
        ["kafka"] = ("kafka", ArchEdgeKind.Queue), ["amqp"] = ("amqp", ArchEdgeKind.Queue), ["amqps"] = ("amqp", ArchEdgeKind.Queue),
        ["nats"] = ("nats", ArchEdgeKind.Queue),
    };

    private static readonly Dictionary<int, string> PortProtocols = new()
    {
        [6379] = "redis", [6380] = "redis", [5432] = "postgres", [3306] = "mysql", [27017] = "mongodb", [11211] = "memcached",
        [9092] = "kafka", [9093] = "kafka", [29092] = "kafka", [5672] = "amqp", [5671] = "amqp", [4222] = "nats",
    };

    /// <summary>Image name fragment → store type; the first match wins, checked against the main container's image only.</summary>
    private static readonly (string Fragment, string Type, string Protocol)[] StoreImages =
    [
        ("redis", "Redis", "redis"), ("valkey", "Redis", "redis"), ("postgis", "PostgreSQL", "postgres"), ("postgres", "PostgreSQL", "postgres"),
        ("mysql", "MySQL", "mysql"), ("mariadb", "MySQL", "mysql"), ("mongo", "MongoDB", "mongodb"), ("memcached", "Memcached", "memcached"),
        ("kafka", "Kafka", "kafka"), ("redpanda", "Kafka", "kafka"), ("rabbitmq", "RabbitMQ", "amqp"), ("nats", "NATS", "nats"),
        ("elasticsearch", "Elasticsearch", "http"), ("opensearch", "OpenSearch", "http"),
    ];

    private static readonly string[] TeamLabels = ["team", "app.kubernetes.io/team", "owner"];

    public static ArchitectureGraph Build(K8sArchitectureInputs inputs, IReadOnlyList<string> scope, ArchOverlay overlay)
    {
        var inScope = scope.Count == 0 ? null : new HashSet<string>(scope, StringComparer.Ordinal);
        bool InScope(string ns) => inScope is null || inScope.Contains(ns);

        var nodes = new Dictionary<string, ArchNode>(StringComparer.Ordinal);
        var edges = new List<ArchEdge>();
        var edgeIds = new HashSet<string>(StringComparer.Ordinal);
        var services = inputs.Services.ToDictionary(s => (s.Namespace, s.Name));

        void AddEdge(ArchEdge edge)
        {
            if (edge.From != edge.To && edgeIds.Add(edge.Id))
            {
                edges.Add(edge);
            }
        }

        // Nodes for every workload in scope, in a stable order.
        var workloads = inputs.Workloads
            .OrderBy(w => w.Namespace, StringComparer.Ordinal).ThenBy(w => w.Name, StringComparer.Ordinal).ToList();
        foreach (var w in workloads.Where(w => InScope(w.Namespace)))
        {
            nodes[WorkloadId(w)] = WorkloadNode(w, inScope: true);
        }

        // Service → the workloads behind it (selector ⊆ pod labels, same namespace); a node per target on demand.
        IReadOnlyList<ArchNode> Targets(K8sArchService svc)
        {
            if (svc.ExternalName is { Length: > 0 } externalName)
            {
                var id = "x:" + externalName.ToLowerInvariant();
                return [nodes.TryGetValue(id, out var existing) ? existing
                    : nodes[id] = new ArchNode(id, ArchNodeKind.External, externalName.ToLowerInvariant(), null,
                        $"ExternalName · {svc.Name}", ArchHealth.Unknown, null, null, null, null, false)];
            }

            var backing = svc.Selector.Count == 0 ? [] : workloads
                .Where(w => w.Namespace == svc.Namespace && svc.Selector.All(kv => w.PodLabels.TryGetValue(kv.Key, out var v) && v == kv.Value))
                .ToList();
            if (backing.Count == 0)
            {
                var id = $"s:{svc.Namespace}/{svc.Name}";
                return [nodes.TryGetValue(id, out var existing) ? existing
                    : nodes[id] = new ArchNode(id, ArchNodeKind.Service, svc.Name, svc.Namespace, "Service · no pods",
                        ArchHealth.Unknown, null, null, null, null, InScope(svc.Namespace))];
            }

            return backing.Select(w => nodes.TryGetValue(WorkloadId(w), out var existing) ? existing
                : nodes[WorkloadId(w)] = WorkloadNode(w, InScope(w.Namespace))).ToList();
        }

        foreach (var ingress in inputs.Ingresses.Where(i => InScope(i.Namespace))
                     .OrderBy(i => i.Namespace, StringComparer.Ordinal).ThenBy(i => i.Name, StringComparer.Ordinal))
        {
            var id = $"i:{ingress.Namespace}/{ingress.Name}";
            var hosts = ingress.Hosts.Count;
            nodes[id] = new ArchNode(id, ArchNodeKind.Ingress, ingress.Name, ingress.Namespace,
                $"Ingress · {hosts.ToString(CultureInfo.InvariantCulture)} host{(hosts == 1 ? "" : "s")}",
                ArchHealth.Unknown, null, null, null, null, true);
            foreach (var backend in ingress.Services.Distinct(StringComparer.Ordinal))
            {
                if (!services.TryGetValue((ingress.Namespace, backend), out var svc))
                {
                    continue;
                }

                foreach (var target in Targets(svc))
                {
                    AddEdge(new ArchEdge(ArchEdge.MakeId(id, target.Id), id, target.Id, ArchEdgeKind.Http, "HTTP", $"Ingress {ingress.Name}", false));
                }
            }
        }

        foreach (var w in workloads.Where(w => InScope(w.Namespace)))
        {
            var from = WorkloadId(w);
            foreach (var env in w.Env)
            {
                if (env.Value is not { Length: > 0 } value)
                {
                    continue;
                }

                foreach (var endpoint in Endpoints(value))
                {
                    if (ResolveService(endpoint.Host, w.Namespace, services) is { } svc)
                    {
                        foreach (var target in Targets(svc))
                        {
                            var (kind, label) = Classify(endpoint, target, svc);
                            AddEdge(new ArchEdge(ArchEdge.MakeId(from, target.Id), from, target.Id, kind, label, $"env {env.Name}", false));
                        }
                    }
                    else if (IsExternal(endpoint))
                    {
                        var id = "x:" + endpoint.Host;
                        var scheme = endpoint.Scheme!.ToUpperInvariant();
                        if (!nodes.ContainsKey(id))
                        {
                            nodes[id] = new ArchNode(id, ArchNodeKind.External, endpoint.Host, null, $"external · {scheme}",
                                ArchHealth.Unknown, null, null, null, null, false);
                        }

                        AddEdge(new ArchEdge(ArchEdge.MakeId(from, id), from, id, ArchEdgeKind.Http, HttpLabel(endpoint), $"env {env.Name}", false));
                    }
                }
            }
        }

        var dismissed = new HashSet<string>(overlay.Dismissed, StringComparer.Ordinal);
        var dismissedCount = edges.RemoveAll(e => dismissed.Contains(e.Id));

        foreach (var manual in overlay.Manual)
        {
            if (!nodes.ContainsKey(manual.From))
            {
                continue;
            }

            if (!nodes.ContainsKey(manual.To))
            {
                if (!manual.To.StartsWith("x:", StringComparison.Ordinal) || manual.To.Length <= 2)
                {
                    continue;
                }

                var host = manual.To[2..];
                nodes[manual.To] = new ArchNode(manual.To, ArchNodeKind.External, host, null, "external",
                    ArchHealth.Unknown, null, null, null, null, false);
            }

            var label = string.IsNullOrWhiteSpace(manual.Label) ? DefaultLabel(manual.Kind) : manual.Label.Trim();
            AddEdge(new ArchEdge(ArchEdge.MakeId(manual.From, manual.To), manual.From, manual.To, manual.Kind, label, "manual", true));
        }

        return new ArchitectureGraph(nodes.Values.ToList(), edges, dismissedCount);
    }

    public static string DefaultLabel(ArchEdgeKind kind) => kind switch
    {
        ArchEdgeKind.Queue => "queue",
        ArchEdgeKind.Store => "store",
        _ => "HTTP",
    };

    private static string WorkloadId(K8sArchWorkload w) => $"w:{w.Namespace}/{w.Kind}/{w.Name}";

    private static ArchNode WorkloadNode(K8sArchWorkload w, bool inScope)
    {
        var image = w.Images.FirstOrDefault() ?? "";
        var store = StoreOf(image);
        var health = w.Desired == 0 ? ArchHealth.Idle
            : w.Unhealthy ? ArchHealth.Failing
            : w.Ready < w.Desired ? ArchHealth.Degraded
            : ArchHealth.Ok;
        var counts = $"{w.Ready.ToString(CultureInfo.InvariantCulture)}/{w.Desired.ToString(CultureInfo.InvariantCulture)}";
        var subtitle = store is { } s ? $"{s.Type} · {w.Kind} · {w.Namespace}"
            : health == ArchHealth.Idle ? $"{w.Kind} · scaled to 0"
            : health == ArchHealth.Failing ? $"{counts} · unavailable"
            : $"{w.Kind} · {counts}";
        var team = TeamLabels.Select(l => w.Labels.GetValueOrDefault(l)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        return new ArchNode(WorkloadId(w), store is null ? ArchNodeKind.Workload : ArchNodeKind.Store, w.Name, w.Namespace, subtitle,
            health, w.Kind, store?.Type, team, ImageTag(image), inScope);
    }

    private static (string Type, string Protocol)? StoreOf(string image)
    {
        var name = ImageName(image);
        foreach (var (fragment, type, protocol) in StoreImages)
        {
            if (name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return (type, protocol);
            }
        }

        return null;
    }

    /// <summary>"ghcr.io/org/redis-ha:7@sha256:…" → "redis-ha": the last path segment without tag or digest.</summary>
    private static string ImageName(string image)
    {
        var at = image.IndexOf('@', StringComparison.Ordinal);
        var withoutDigest = at >= 0 ? image[..at] : image;
        var lastSlash = withoutDigest.LastIndexOf('/');
        var last = withoutDigest[(lastSlash + 1)..];
        var colon = last.IndexOf(':', StringComparison.Ordinal);
        return colon >= 0 ? last[..colon] : last;
    }

    private static string? ImageTag(string image)
    {
        if (image.Contains('@', StringComparison.Ordinal))
        {
            return null;
        }

        var last = image[(image.LastIndexOf('/') + 1)..];
        var colon = last.IndexOf(':', StringComparison.Ordinal);
        return colon >= 0 && colon < last.Length - 1 ? last[(colon + 1)..] : null;
    }

    private static (ArchEdgeKind Kind, string Label) Classify(Endpoint endpoint, ArchNode target, K8sArchService svc)
    {
        var port = endpoint.Port ?? (svc.Ports.Count > 0 ? svc.Ports[0] : null);
        string? protocol = null;
        if (endpoint.Scheme is { } scheme && Protocols.TryGetValue(scheme, out var byScheme))
        {
            protocol = byScheme.Name;
        }
        else if (endpoint.Scheme is null && target.StoreType is not null)
        {
            protocol = StoreImages.First(s => s.Type == target.StoreType).Protocol;
        }

        if (protocol is null && endpoint.Scheme is null && port is { } p && PortProtocols.TryGetValue(p, out var byPort))
        {
            protocol = byPort;
        }

        if (protocol is null || protocol == "http")
        {
            return (ArchEdgeKind.Http, HttpLabel(endpoint with { Port = port }));
        }

        var kind = Protocols[protocol].Kind;
        return (kind, port is { } q ? $"{protocol} · {q.ToString(CultureInfo.InvariantCulture)}" : protocol);
    }

    private static string HttpLabel(Endpoint endpoint)
    {
        var name = endpoint.Scheme is { } s && HttpSchemes.Contains(s, StringComparer.OrdinalIgnoreCase) ? s.ToUpperInvariant() : "HTTP";
        return endpoint.Port is { } port ? $"{name} · {port.ToString(CultureInfo.InvariantCulture)}" : name;
    }

    private static bool IsExternal(Endpoint endpoint) =>
        endpoint.Scheme is { } scheme && HttpSchemes.Contains(scheme, StringComparer.OrdinalIgnoreCase)
        && endpoint.Host.Contains('.', StringComparison.Ordinal)
        && !endpoint.Host.EndsWith(".svc", StringComparison.Ordinal)
        && !endpoint.Host.EndsWith(".cluster.local", StringComparison.Ordinal)
        && !endpoint.Host.EndsWith(".local", StringComparison.Ordinal)
        && !endpoint.Host.EndsWith(".internal", StringComparison.Ordinal);

    /// <summary>
    /// "svc" (caller's namespace), "svc.ns", "svc.ns.svc[.cluster.local]" and StatefulSet pod names
    /// "pod.svc.ns.svc[.cluster.local]" resolve to a Service; anything else is not in the cluster.
    /// </summary>
    private static K8sArchService? ResolveService(string host, string callerNamespace, Dictionary<(string, string), K8sArchService> services)
    {
        var name = host;
        var qualified = false;
        if (name.EndsWith(".cluster.local", StringComparison.Ordinal))
        {
            name = name[..^".cluster.local".Length];
        }

        if (name.EndsWith(".svc", StringComparison.Ordinal))
        {
            name = name[..^".svc".Length];
            qualified = true;
        }

        var parts = name.Split('.');
        (string Ns, string Name)? key = parts.Length switch
        {
            1 => (callerNamespace, parts[0]),
            2 => (parts[1], parts[0]),
            3 when qualified => (parts[2], parts[1]),
            _ => null,
        };
        return key is { } k && services.TryGetValue(k, out var svc) ? svc : null;
    }

    /// <summary>Hosts (with port and scheme when present) named by an env value. Plain words, paths, loopback and IPs yield nothing.</summary>
    public static IReadOnlyList<Endpoint> Endpoints(string value)
    {
        var found = new List<Endpoint>();
        if (value.Contains('=', StringComparison.Ordinal) && value.Contains(';', StringComparison.Ordinal) && !value.Contains("://", StringComparison.Ordinal))
        {
            AdoEndpoints(value, found);
            return found;
        }

        foreach (var token in TokenSplit().Split(value))
        {
            if (token.Length == 0)
            {
                continue;
            }

            var schemeEnd = token.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd > 0)
            {
                var scheme = token[..schemeEnd].ToLowerInvariant();
                var rest = token[(schemeEnd + 3)..];
                var authorityEnd = rest.IndexOfAny(['/', '?', '#']);
                var authority = authorityEnd >= 0 ? rest[..authorityEnd] : rest;
                var at = authority.LastIndexOf('@');
                if (at >= 0)
                {
                    authority = authority[(at + 1)..];
                }

                Add(found, authority, scheme, requireQualified: false);
            }
            else if (HostPort().IsMatch(token))
            {
                Add(found, token, null, requireQualified: false);
            }
            else if (token.EndsWith(".svc", StringComparison.OrdinalIgnoreCase) || token.EndsWith(".svc.cluster.local", StringComparison.OrdinalIgnoreCase))
            {
                Add(found, token, null, requireQualified: true);
            }
        }

        return found;
    }

    private static void AdoEndpoints(string value, List<Endpoint> found)
    {
        string? host = null;
        int? port = null;
        foreach (var pair in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            var key = pair[..eq].Trim().ToLowerInvariant();
            var v = pair[(eq + 1)..].Trim();
            if (HostKeys.Contains(key))
            {
                host = v;
            }
            else if (key == "port" && int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var p))
            {
                port = p;
            }
        }

        if (host is null)
        {
            return;
        }

        if (host.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
        {
            host = host[4..];
        }

        foreach (var h in host.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // SQL Server writes "host,1433"; the comma split leaves the port as its own entry.
            if (int.TryParse(h, NumberStyles.None, CultureInfo.InvariantCulture, out var commaPort))
            {
                if (found.Count > 0 && found[^1].Port is null)
                {
                    found[^1] = found[^1] with { Port = commaPort };
                }

                continue;
            }

            Add(found, h, null, requireQualified: false);
            if (port is { } p && found.Count > 0 && found[^1].Port is null)
            {
                found[^1] = found[^1] with { Port = p };
            }
        }
    }

    private static void Add(List<Endpoint> found, string hostPort, string? scheme, bool requireQualified)
    {
        var host = hostPort;
        int? port = null;
        var colon = hostPort.LastIndexOf(':');
        if (colon > 0)
        {
            if (!int.TryParse(hostPort[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var p) || p is < 1 or > 65535)
            {
                return;
            }

            host = hostPort[..colon];
            port = p;
        }

        host = host.ToLowerInvariant();
        if (!HostName().IsMatch(host) || host == "localhost" || NumericHost().IsMatch(host)
            || (requireQualified && !host.Contains('.', StringComparison.Ordinal)))
        {
            return;
        }

        found.Add(new Endpoint(host, port, scheme));
    }

    [GeneratedRegex(@"[\s,;]+")]
    private static partial Regex TokenSplit();

    [GeneratedRegex(@"^[A-Za-z0-9.-]+:\d{1,5}$")]
    private static partial Regex HostPort();

    [GeneratedRegex(@"^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)*$")]
    private static partial Regex HostName();

    [GeneratedRegex(@"^[0-9.]+$")]
    private static partial Regex NumericHost();
}
