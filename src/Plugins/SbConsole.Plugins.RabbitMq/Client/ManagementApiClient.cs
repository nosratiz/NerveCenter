using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SbConsole.Plugins.RabbitMq.Client;

/// <summary>
/// HttpClient over the RabbitMQ management HTTP API (design spec §4). Every method takes the
/// resolved settings of one connection; nothing is pre-configured. JSON is read into private DTOs
/// and mapped to the Client/ domain records here -- DTOs never leave this class. Missing counts
/// map to 0 and missing rates to null (a fresh broker or an idle queue has no samples yet).
/// Every vhost and name goes into the path through <see cref="Uri.EscapeDataString"/>, so the
/// "/" vhost travels as "%2F". Non-2xx responses throw <see cref="ManagementApiException"/>.
/// </summary>
internal sealed class ManagementApiClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    // One handler per (management URL, verify flag): pages poll, so a handler per request would
    // leak sockets. Credentials are per request (Authorization header), so users share the pool.
    private static readonly ConcurrentDictionary<(string BaseUri, bool VerifyCert), HttpMessageHandler> SharedHandlers = new();

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new LenientInt64Converter(), new LenientDoubleConverter(), new LenientBooleanConverter() },
    };

    private readonly Func<RabbitConnectionSettings, HttpMessageHandler> _handlerFactory;

    public ManagementApiClient()
        : this(DefaultHandlerFactory)
    {
    }

    public ManagementApiClient(Func<RabbitConnectionSettings, HttpMessageHandler> handlerFactory)
    {
        _handlerFactory = handlerFactory;
    }

    internal static HttpMessageHandler DefaultHandlerFactory(RabbitConnectionSettings settings) =>
        SharedHandlers.GetOrAdd((settings.ManagementBaseUri.AbsoluteUri, settings.VerifyCert), static key =>
        {
            var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) };
            if (!key.VerifyCert)
            {
                // The connection explicitly opted out of certificate verification (verifyCert=false).
                handler.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
            }

            return handler;
        });

    // ---------------------------------------------------------------- probe

    public async Task<ManagementProbe> ProbeAsync(RabbitConnectionSettings settings, CancellationToken ct = default)
    {
        var overview = await GetAsync<OverviewDto>(settings, "api/overview", ct);
        var whoami = await GetAsync<WhoAmIDto>(settings, "api/whoami", ct);
        var vhost = Seg(settings.Vhost);
        var exchanges = await GetAsync<List<NameDto>>(settings, $"api/exchanges/{vhost}?columns=name", ct);
        var queues = await GetAsync<List<NameDto>>(settings, $"api/queues/{vhost}?columns=name", ct);

        return new ManagementProbe(
            RabbitVersionOf(overview),
            overview.ClusterName ?? "",
            exchanges.Count,
            queues.Count,
            TagsOf(whoami.Tags));
    }

    // ---------------------------------------------------------------- reads

    public async Task<IReadOnlyList<string>> ListVhostsAsync(RabbitConnectionSettings settings, CancellationToken ct = default)
    {
        var vhosts = await GetAsync<List<NameDto>>(settings, "api/vhosts", ct);
        return vhosts.Select(v => v.Name ?? "").ToList();
    }

    public async Task<BrokerOverview> GetOverviewAsync(RabbitConnectionSettings settings, CancellationToken ct = default)
    {
        var o = await GetAsync<OverviewDto>(settings, "api/overview", ct);
        var stats = o.MessageStats;
        var drop = stats?.DropUnroutableDetails?.Rate;
        var ret = stats?.ReturnUnroutableDetails?.Rate;

        return new BrokerOverview(
            ClusterName: o.ClusterName ?? "",
            RabbitVersion: RabbitVersionOf(o),
            ErlangVersion: o.ErlangVersion,
            PublishRate: stats?.PublishDetails?.Rate,
            DeliverRate: stats?.DeliverGetDetails?.Rate,
            AckRate: stats?.AckDetails?.Rate,
            UnroutableRate: drop is null && ret is null ? null : (drop ?? 0) + (ret ?? 0),
            Connections: o.ObjectTotals?.Connections ?? 0,
            Channels: o.ObjectTotals?.Channels ?? 0,
            Queues: o.ObjectTotals?.Queues ?? 0,
            Exchanges: o.ObjectTotals?.Exchanges ?? 0,
            Consumers: o.ObjectTotals?.Consumers ?? 0,
            MessagesReady: o.QueueTotals?.MessagesReady ?? 0,
            MessagesUnacked: o.QueueTotals?.MessagesUnacknowledged ?? 0,
            ConnectionsOpened: o.ChurnRates?.ConnectionCreated ?? 0,
            ConnectionsClosed: o.ChurnRates?.ConnectionClosed ?? 0,
            ChannelsOpened: o.ChurnRates?.ChannelCreated ?? 0);
    }

    public async Task<IReadOnlyList<NodeSummary>> ListNodesAsync(RabbitConnectionSettings settings, CancellationToken ct = default)
    {
        var nodes = await GetAsync<List<NodeDto>>(settings, "api/nodes", ct);
        return nodes.Select(n => new NodeSummary(
            n.Name ?? "",
            n.Running,
            n.MemUsed,
            n.MemLimit,
            n.MemAlarm,
            n.DiskFree,
            n.DiskFreeLimit,
            n.DiskFreeAlarm,
            n.FdUsed,
            n.FdTotal,
            n.Uptime is { } ms ? TimeSpan.FromMilliseconds(ms) : null)).ToList();
    }

    public async Task<IReadOnlyList<ExchangeSummary>> ListExchangesAsync(RabbitConnectionSettings settings, string vhost, CancellationToken ct = default)
    {
        var exchanges = await GetAsync<List<ExchangeDto>>(settings, $"api/exchanges/{Seg(vhost)}", ct);
        return exchanges.Select(e =>
        {
            var arguments = ToClrDictionary(e.Arguments);
            return new ExchangeSummary(
                e.Name ?? "",
                e.Type ?? "",
                e.Durable,
                e.AutoDelete,
                e.Internal,
                arguments.GetValueOrDefault("alternate-exchange") as string,
                e.MessageStats?.PublishInDetails?.Rate,
                e.MessageStats?.PublishOutDetails?.Rate,
                arguments);
        }).ToList();
    }

    public async Task<IReadOnlyList<QueueSummary>> ListQueuesAsync(RabbitConnectionSettings settings, string? vhost, CancellationToken ct = default)
    {
        var path = vhost is null ? "api/queues" : $"api/queues/{Seg(vhost)}";
        var queues = await GetAsync<List<QueueDto>>(settings, path, ct);
        return queues.Select(q => MapQueue(q, vhost)).ToList();
    }

    public async Task<QueueDetails> GetQueueAsync(RabbitConnectionSettings settings, string vhost, string queue, CancellationToken ct = default)
    {
        var path = $"api/queues/{Seg(vhost)}/{Seg(queue)}";
        var q = await GetAsync<QueueDto>(settings, path, ct);
        var bindings = await GetAsync<List<BindingDto>>(settings, $"{path}/bindings", ct);

        var consumers = (q.ConsumerDetails ?? []).Select(c => new ConsumerInfo(
            c.ConsumerTag ?? "",
            c.ChannelDetails?.Name ?? "",
            (int)c.PrefetchCount,
            c.AckRequired,
            c.Exclusive)).ToList();

        return new QueueDetails(MapQueue(q, vhost), consumers, bindings.Select(MapBinding).ToList());
    }

    public async Task<IReadOnlyList<BindingInfo>> ListBindingsAsync(RabbitConnectionSettings settings, string? vhost, CancellationToken ct = default)
    {
        var path = vhost is null ? "api/bindings" : $"api/bindings/{Seg(vhost)}";
        var bindings = await GetAsync<List<BindingDto>>(settings, path, ct);
        return bindings.Select(MapBinding).ToList();
    }

    public async Task<IReadOnlyList<ShovelInfo>> ListShovelsAsync(RabbitConnectionSettings settings, string vhost, CancellationToken ct = default)
    {
        // Status first: a 404 here means the shovel management plugin isn't enabled, and the
        // caller turns that into an info alert rather than an error.
        var statuses = await GetAsync<List<ShovelStatusDto>>(settings, $"api/shovels/{Seg(vhost)}", ct);
        var definitions = await GetAsync<List<ShovelParameterDto>>(settings, $"api/parameters/shovel/{Seg(vhost)}", ct);

        // A cluster can report a shovel from more than one node; the first row wins.
        var statusByName = new Dictionary<string, ShovelStatusDto>(StringComparer.Ordinal);
        foreach (var status in statuses)
        {
            statusByName.TryAdd(status.Name ?? "", status);
        }

        var result = new List<ShovelInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            var name = definition.Name ?? "";
            if (!seen.Add(name))
            {
                continue;
            }

            statusByName.TryGetValue(name, out var status);
            var v = definition.Value;
            result.Add(new ShovelInfo(
                Name: name,
                State: status is null ? "starting" : StateOf(status.State),
                Reason: status is null ? null : ReasonOf(status.Reason),
                SourceQueue: v?.SrcQueue ?? status?.SrcQueue,
                SourceExchange: v?.SrcExchange ?? status?.SrcExchange,
                SourceUri: FirstUri(v?.SrcUri) ?? status?.SrcUri ?? "",
                DestinationQueue: v?.DestQueue ?? status?.DestQueue,
                DestinationExchange: v?.DestExchange ?? status?.DestExchange,
                DestinationRoutingKey: v?.DestExchangeKey ?? status?.DestExchangeKey,
                DestinationUri: FirstUri(v?.DestUri) ?? status?.DestUri ?? "",
                AckMode: v?.AckMode ?? DefaultShovelAckMode,
                Timestamp: ParseBrokerTime(status?.Timestamp)));
        }

        // Status rows with no dynamic definition (static shovels, or one deleted a moment ago).
        foreach (var (name, status) in statusByName)
        {
            if (seen.Contains(name))
            {
                continue;
            }

            result.Add(new ShovelInfo(
                name,
                StateOf(status.State),
                ReasonOf(status.Reason),
                status.SrcQueue,
                status.SrcExchange,
                status.SrcUri ?? "",
                status.DestQueue,
                status.DestExchange,
                status.DestExchangeKey,
                status.DestUri ?? "",
                DefaultShovelAckMode,
                ParseBrokerTime(status.Timestamp)));
        }

        return result;
    }

    public async Task<IReadOnlyList<PolicyInfo>> ListPoliciesAsync(RabbitConnectionSettings settings, string vhost, CancellationToken ct = default)
    {
        var policies = await GetAsync<List<PolicyDto>>(settings, $"api/policies/{Seg(vhost)}", ct);
        return policies.Select(p => new PolicyInfo(
            p.Name ?? "",
            p.Pattern ?? "",
            p.ApplyTo ?? "all",
            (int)p.Priority,
            ToClrDictionary(p.Definition))).ToList();
    }

    // ---------------------------------------------------------------- writes

    public Task CreateExchangeAsync(RabbitConnectionSettings settings, string vhost, CreateExchangeRequest request, CancellationToken ct = default)
    {
        var arguments = new Dictionary<string, object?>();
        if (!string.IsNullOrEmpty(request.AlternateExchange))
        {
            arguments["alternate-exchange"] = request.AlternateExchange;
        }

        var body = new Dictionary<string, object?>
        {
            ["type"] = request.Type,
            ["durable"] = request.Durable,
            ["auto_delete"] = request.AutoDelete,
            ["internal"] = request.Internal,
            ["arguments"] = arguments,
        };
        return SendAsync(settings, HttpMethod.Put, $"api/exchanges/{Seg(vhost)}/{ExchangeSeg(request.Name)}", body, ct);
    }

    public Task DeleteExchangeAsync(RabbitConnectionSettings settings, string vhost, string exchange, CancellationToken ct = default) =>
        SendAsync(settings, HttpMethod.Delete, $"api/exchanges/{Seg(vhost)}/{ExchangeSeg(exchange)}", null, ct);

    public Task CreateQueueAsync(RabbitConnectionSettings settings, string vhost, CreateQueueRequest request, CancellationToken ct = default)
    {
        var arguments = new Dictionary<string, object?> { ["x-queue-type"] = request.Type };
        AddIfSet(arguments, "x-dead-letter-exchange", request.DeadLetterExchange);
        AddIfSet(arguments, "x-dead-letter-routing-key", request.DeadLetterRoutingKey);
        AddIfSet(arguments, "x-message-ttl", request.MessageTtlMs);
        AddIfSet(arguments, "x-max-length", request.MaxLength);
        AddIfSet(arguments, "x-overflow", request.Overflow);

        var body = new Dictionary<string, object?>
        {
            ["durable"] = request.Durable,
            ["auto_delete"] = request.AutoDelete,
            ["arguments"] = arguments,
        };
        return SendAsync(settings, HttpMethod.Put, $"api/queues/{Seg(vhost)}/{Seg(request.Name)}", body, ct);
    }

    public Task DeleteQueueAsync(RabbitConnectionSettings settings, string vhost, string queue, CancellationToken ct = default) =>
        SendAsync(settings, HttpMethod.Delete, $"api/queues/{Seg(vhost)}/{Seg(queue)}", null, ct);

    public Task PurgeQueueAsync(RabbitConnectionSettings settings, string vhost, string queue, CancellationToken ct = default) =>
        SendAsync(settings, HttpMethod.Delete, $"api/queues/{Seg(vhost)}/{Seg(queue)}/contents", null, ct);

    public Task AddBindingAsync(
        RabbitConnectionSettings settings, string vhost, string exchange, string queue, string routingKey,
        IReadOnlyDictionary<string, object?> arguments, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["routing_key"] = routingKey,
            ["arguments"] = arguments,
        };
        return SendAsync(settings, HttpMethod.Post, $"api/bindings/{Seg(vhost)}/e/{ExchangeSeg(exchange)}/q/{Seg(queue)}", body, ct);
    }

    /// <summary>
    /// <paramref name="propertiesKey"/> is the binding's properties_key exactly as a listing
    /// returned it. It is itself percent-encoded ("order.%2A.amended"), and the broker compares
    /// it after decoding the path once, so it is escaped again like any other segment.
    /// </summary>
    public Task RemoveBindingAsync(
        RabbitConnectionSettings settings, string vhost, string exchange, string queue, string propertiesKey,
        CancellationToken ct = default) =>
        SendAsync(settings, HttpMethod.Delete,
            $"api/bindings/{Seg(vhost)}/e/{ExchangeSeg(exchange)}/q/{Seg(queue)}/{Seg(propertiesKey)}", null, ct);

    public Task CreateShovelAsync(RabbitConnectionSettings settings, string vhost, CreateShovelRequest request, CancellationToken ct = default)
    {
        var toQueue = !string.IsNullOrEmpty(request.DestinationQueue);
        var toExchange = !string.IsNullOrEmpty(request.DestinationExchange);
        if (toQueue == toExchange)
        {
            throw new ArgumentException("A shovel needs exactly one destination: a queue or an exchange.", nameof(request));
        }

        // "amqp:///{vhost}" (empty host) means this broker; the vhost must be URI-encoded.
        var localUri = "amqp:///" + Uri.EscapeDataString(vhost);
        var value = new Dictionary<string, object?>
        {
            ["src-uri"] = request.SourceUri ?? localUri,
            ["src-queue"] = request.SourceQueue,
            ["dest-uri"] = request.DestinationUri ?? localUri,
        };
        if (toQueue)
        {
            value["dest-queue"] = request.DestinationQueue;
        }
        else
        {
            value["dest-exchange"] = request.DestinationExchange;
            AddIfSet(value, "dest-exchange-key", request.DestinationRoutingKey);
        }

        value["ack-mode"] = request.AckMode;
        value["src-delete-after"] = request.SourceDeleteAfter;

        var body = new Dictionary<string, object?> { ["value"] = value };
        return SendAsync(settings, HttpMethod.Put, $"api/parameters/shovel/{Seg(vhost)}/{Seg(request.Name)}", body, ct);
    }

    public Task DeleteShovelAsync(RabbitConnectionSettings settings, string vhost, string name, CancellationToken ct = default) =>
        SendAsync(settings, HttpMethod.Delete, $"api/parameters/shovel/{Seg(vhost)}/{Seg(name)}", null, ct);

    public Task RestartShovelAsync(RabbitConnectionSettings settings, string vhost, string name, CancellationToken ct = default) =>
        SendAsync(settings, HttpMethod.Delete, $"api/shovels/vhost/{Seg(vhost)}/{Seg(name)}/restart", null, ct);

    // ---------------------------------------------------------------- transport

    private async Task<T> GetAsync<T>(RabbitConnectionSettings settings, string relativePath, CancellationToken ct)
    {
        using var client = CreateClient(settings);
        using var request = CreateRequest(settings, HttpMethod.Get, relativePath, body: null);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, request, ct);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var result = await JsonSerializer.DeserializeAsync<T>(stream, ReadOptions, ct);
        return result ?? throw new JsonException($"Management API returned an empty body for {relativePath}.");
    }

    private async Task SendAsync(RabbitConnectionSettings settings, HttpMethod method, string relativePath, object? body, CancellationToken ct)
    {
        using var client = CreateClient(settings);
        using var request = CreateRequest(settings, method, relativePath, body);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, request, ct);
    }

    private HttpClient CreateClient(RabbitConnectionSettings settings) =>
        new(_handlerFactory(settings), disposeHandler: false) { Timeout = RequestTimeout };

    private static HttpRequestMessage CreateRequest(RabbitConnectionSettings settings, HttpMethod method, string relativePath, object? body)
    {
        var request = new HttpRequestMessage(method, new Uri(settings.ManagementBaseUri, relativePath));
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.Username}:{settings.Password}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);

        // Reads only: DELETE /api/queues/{vhost}/{name}/contents answers 406 Not Acceptable to
        // "Accept: application/json" (it produces no body), confirmed against 3.13.7.
        if (method == HttpMethod.Get)
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }

        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, HttpRequestMessage request, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string? reason = null;
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("reason", out var reasonElement))
            {
                reason = reasonElement.ValueKind == JsonValueKind.String ? reasonElement.GetString() : reasonElement.GetRawText();
            }
        }
        catch (JsonException)
        {
            // Not JSON (a proxy's HTML error page, say): no reason to report.
        }

        throw new ManagementApiException(
            (int)response.StatusCode, request.Method.Method, request.RequestUri!.AbsolutePath, reason);
    }

    // ---------------------------------------------------------------- mapping

    private const string DefaultShovelAckMode = "on-confirm";

    private static readonly string[] KnownShovelStates = ["running", "starting", "terminated"];

    private static string Seg(string value) => Uri.EscapeDataString(value);

    // The default exchange is "" in listings but must be addressed as "amq.default" in paths.
    private static string ExchangeSeg(string exchange) => exchange.Length == 0 ? "amq.default" : Seg(exchange);

    private static void AddIfSet(Dictionary<string, object?> target, string key, object? value)
    {
        if (value is not null && value is not "")
        {
            target[key] = value;
        }
    }

    private static string RabbitVersionOf(OverviewDto overview) =>
        overview.RabbitmqVersion ?? overview.ProductVersion ?? overview.ManagementVersion ?? "";

    // 3.x returns tags as an array; older brokers send one comma-separated string.
    private static IReadOnlyList<string> TagsOf(JsonElement? tags) => tags switch
    {
        { ValueKind: JsonValueKind.Array } array => array.EnumerateArray()
            .Where(t => t.ValueKind == JsonValueKind.String)
            .Select(t => t.GetString()!)
            .Where(t => t.Length > 0)
            .ToList(),
        { ValueKind: JsonValueKind.String } text => text.GetString()!
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        _ => [],
    };

    private static QueueSummary MapQueue(QueueDto q, string? requestedVhost)
    {
        var arguments = ToClrDictionary(q.Arguments);
        var policy = ToClrDictionary(q.EffectivePolicyDefinition);

        return new QueueSummary(
            Vhost: q.Vhost ?? requestedVhost ?? "",
            Name: q.Name ?? "",
            Type: q.Type ?? arguments.GetValueOrDefault("x-queue-type") as string ?? "classic",
            Durable: q.Durable,
            AutoDelete: q.AutoDelete,
            Exclusive: q.Exclusive,
            Ready: q.MessagesReady,
            Unacked: q.MessagesUnacknowledged,
            Consumers: (int)q.Consumers,
            PublishRate: q.MessageStats?.PublishDetails?.Rate,
            AckRate: q.MessageStats?.AckDetails?.Rate,
            RedeliverRate: q.MessageStats?.RedeliverDetails?.Rate,
            DeadLetterExchange: ResolveString(arguments, "x-dead-letter-exchange", policy, "dead-letter-exchange"),
            DeadLetterRoutingKey: ResolveString(arguments, "x-dead-letter-routing-key", policy, "dead-letter-routing-key"),
            MessageTtlMs: ResolveLong(arguments, "x-message-ttl", policy, "message-ttl"),
            MaxLength: ResolveLong(arguments, "x-max-length", policy, "max-length"),
            Overflow: ResolveString(arguments, "x-overflow", policy, "overflow"),
            Lazy: ResolveString(arguments, "x-queue-mode", policy, "queue-mode") == "lazy",
            Policy: string.IsNullOrEmpty(q.Policy) ? null : q.Policy,
            IdleSince: ParseBrokerTime(q.IdleSince),
            MemoryBytes: q.Memory,
            ReplicaCount: q.Members?.Count,
            HeadMessageTimestamp: q.HeadMessageTimestamp is { ValueKind: JsonValueKind.Number } ts && ts.TryGetInt64(out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : null,
            Arguments: arguments,
            EffectivePolicyDefinition: policy);
    }

    // A queue argument beats a policy for these keys (RabbitMQ semantics), so read it first.
    private static string? ResolveString(
        IReadOnlyDictionary<string, object?> arguments, string argumentKey,
        IReadOnlyDictionary<string, object?> policy, string policyKey) =>
        arguments.GetValueOrDefault(argumentKey) as string ?? policy.GetValueOrDefault(policyKey) as string;

    private static long? ResolveLong(
        IReadOnlyDictionary<string, object?> arguments, string argumentKey,
        IReadOnlyDictionary<string, object?> policy, string policyKey) =>
        AsLong(arguments.GetValueOrDefault(argumentKey)) ?? AsLong(policy.GetValueOrDefault(policyKey));

    private static long? AsLong(object? value) => value switch
    {
        long l => l,
        double d => (long)d,
        string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };

    private static BindingInfo MapBinding(BindingDto b) => new(
        b.Source ?? "",
        b.Destination ?? "",
        b.DestinationType ?? "",
        b.RoutingKey ?? "",
        ToClrDictionary(b.Arguments),
        b.PropertiesKey ?? "");

    private static string StateOf(string? state) =>
        state is not null && KnownShovelStates.Contains(state) ? state : "unknown";

    private static string? ReasonOf(JsonElement? reason) => reason switch
    {
        null => null,
        { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
        { ValueKind: JsonValueKind.String } text => text.GetString(),
        { } other => other.GetRawText(),
    };

    // src-uri/dest-uri may be one URI or a list of fail-over URIs.
    private static string? FirstUri(JsonElement? uri) => uri switch
    {
        { ValueKind: JsonValueKind.String } text => text.GetString(),
        { ValueKind: JsonValueKind.Array } list => list.EnumerateArray()
            .Where(u => u.ValueKind == JsonValueKind.String)
            .Select(u => u.GetString())
            .FirstOrDefault(),
        _ => null,
    };

    // Stats timestamps come as "2026-09-25 20:11:56" (UTC, no zone) in listings and as ISO 8601
    // ("2026-09-25T20:12:00.272+00:00") on single-queue reads; both are accepted.
    private static DateTimeOffset? ParseBrokerTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        const DateTimeStyles utc = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;
        if (DateTimeOffset.TryParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, utc, out var exact))
        {
            return exact;
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, utc, out var parsed) ? parsed : null;
    }

    // Callers never see JsonElement: arguments/definitions become plain CLR values. Anything
    // that isn't a JSON object (older brokers render an empty definition as []) is empty.
    private static Dictionary<string, object?> ToClrDictionary(JsonElement? element)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (element is { ValueKind: JsonValueKind.Object } obj)
        {
            foreach (var property in obj.EnumerateObject())
            {
                result[property.Name] = ToClr(property.Value);
            }
        }

        return result;
    }

    private static object? ToClr(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        // The (object) cast matters: without it the conditional unifies to double and every integer
        // (x-message-ttl, max-length) would surface as a double.
        JsonValueKind.Number => element.TryGetInt64(out var l) ? (object)l : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => element.EnumerateArray().Select(ToClr).ToList(),
        JsonValueKind.Object => ToClrDictionary(element),
        _ => null,
    };

    // ---------------------------------------------------------------- lenient converters

    // The management API occasionally puts a string where a number belongs (fd_used on some
    // platforms, "undefined" on a node that's down). A stats glitch must not fail the page.
    private sealed class LenientInt64Converter : JsonConverter<long>
    {
        public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType switch
            {
                JsonTokenType.Number when reader.TryGetInt64(out var l) => l,
                JsonTokenType.Number => (long)reader.GetDouble(),
                JsonTokenType.String when long.TryParse(reader.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => SkipAndDefault<long>(ref reader),
            };

        public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
    }

    private sealed class LenientDoubleConverter : JsonConverter<double>
    {
        public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType switch
            {
                JsonTokenType.Number => reader.GetDouble(),
                JsonTokenType.String when double.TryParse(reader.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => SkipAndDefault<double>(ref reader),
            };

        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
    }

    private sealed class LenientBooleanConverter : JsonConverter<bool>
    {
        public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType switch
            {
                JsonTokenType.True => true,
                JsonTokenType.False => false,
                JsonTokenType.String => string.Equals(reader.GetString(), "true", StringComparison.OrdinalIgnoreCase),
                _ => SkipAndDefault<bool>(ref reader),
            };

        public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) => writer.WriteBooleanValue(value);
    }

    private static T SkipAndDefault<T>(ref Utf8JsonReader reader)
    {
        reader.Skip();
        return default!;
    }

    // ---------------------------------------------------------------- DTOs (never leave this class)

    private sealed class NameDto
    {
        public string? Name { get; set; }
    }

    private sealed class RateDto
    {
        public double? Rate { get; set; }
    }

    private sealed class WhoAmIDto
    {
        public JsonElement? Tags { get; set; }
    }

    private sealed class OverviewDto
    {
        public string? ClusterName { get; set; }

        public string? RabbitmqVersion { get; set; }

        public string? ProductVersion { get; set; }

        public string? ManagementVersion { get; set; }

        public string? ErlangVersion { get; set; }

        public OverviewMessageStatsDto? MessageStats { get; set; }

        public ChurnRatesDto? ChurnRates { get; set; }

        public QueueTotalsDto? QueueTotals { get; set; }

        public ObjectTotalsDto? ObjectTotals { get; set; }
    }

    private sealed class OverviewMessageStatsDto
    {
        public RateDto? PublishDetails { get; set; }

        public RateDto? DeliverGetDetails { get; set; }

        public RateDto? AckDetails { get; set; }

        public RateDto? DropUnroutableDetails { get; set; }

        public RateDto? ReturnUnroutableDetails { get; set; }
    }

    private sealed class ChurnRatesDto
    {
        public long ConnectionCreated { get; set; }

        public long ConnectionClosed { get; set; }

        public long ChannelCreated { get; set; }
    }

    private sealed class QueueTotalsDto
    {
        public long MessagesReady { get; set; }

        public long MessagesUnacknowledged { get; set; }
    }

    private sealed class ObjectTotalsDto
    {
        public long Connections { get; set; }

        public long Channels { get; set; }

        public long Queues { get; set; }

        public long Exchanges { get; set; }

        public long Consumers { get; set; }
    }

    private sealed class NodeDto
    {
        public string? Name { get; set; }

        public bool Running { get; set; }

        public long MemUsed { get; set; }

        public long MemLimit { get; set; }

        public bool MemAlarm { get; set; }

        public long DiskFree { get; set; }

        public long DiskFreeLimit { get; set; }

        public bool DiskFreeAlarm { get; set; }

        public long FdUsed { get; set; }

        public long FdTotal { get; set; }

        public long? Uptime { get; set; }
    }

    private sealed class ExchangeDto
    {
        public string? Name { get; set; }

        public string? Type { get; set; }

        public bool Durable { get; set; }

        public bool AutoDelete { get; set; }

        public bool Internal { get; set; }

        public JsonElement? Arguments { get; set; }

        public ExchangeMessageStatsDto? MessageStats { get; set; }
    }

    private sealed class ExchangeMessageStatsDto
    {
        public RateDto? PublishInDetails { get; set; }

        public RateDto? PublishOutDetails { get; set; }
    }

    private sealed class QueueDto
    {
        public string? Vhost { get; set; }

        public string? Name { get; set; }

        public string? Type { get; set; }

        public bool Durable { get; set; }

        public bool AutoDelete { get; set; }

        public bool Exclusive { get; set; }

        public long MessagesReady { get; set; }

        public long MessagesUnacknowledged { get; set; }

        public long Consumers { get; set; }

        public QueueMessageStatsDto? MessageStats { get; set; }

        public JsonElement? Arguments { get; set; }

        public JsonElement? EffectivePolicyDefinition { get; set; }

        public string? Policy { get; set; }

        public string? IdleSince { get; set; }

        public long Memory { get; set; }

        public List<string>? Members { get; set; }

        public JsonElement? HeadMessageTimestamp { get; set; }

        public List<ConsumerDto>? ConsumerDetails { get; set; }
    }

    private sealed class QueueMessageStatsDto
    {
        public RateDto? PublishDetails { get; set; }

        public RateDto? AckDetails { get; set; }

        public RateDto? RedeliverDetails { get; set; }
    }

    private sealed class ConsumerDto
    {
        public string? ConsumerTag { get; set; }

        public ChannelDetailsDto? ChannelDetails { get; set; }

        public long PrefetchCount { get; set; }

        public bool AckRequired { get; set; }

        public bool Exclusive { get; set; }
    }

    private sealed class ChannelDetailsDto
    {
        public string? Name { get; set; }
    }

    private sealed class BindingDto
    {
        public string? Source { get; set; }

        public string? Destination { get; set; }

        public string? DestinationType { get; set; }

        public string? RoutingKey { get; set; }

        public JsonElement? Arguments { get; set; }

        public string? PropertiesKey { get; set; }
    }

    private sealed class PolicyDto
    {
        public string? Name { get; set; }

        public string? Pattern { get; set; }

        [JsonPropertyName("apply-to")]
        public string? ApplyTo { get; set; }

        public long Priority { get; set; }

        public JsonElement? Definition { get; set; }
    }

    private sealed class ShovelStatusDto
    {
        public string? Name { get; set; }

        public string? State { get; set; }

        public JsonElement? Reason { get; set; }

        public string? Timestamp { get; set; }

        public string? SrcUri { get; set; }

        public string? SrcQueue { get; set; }

        public string? SrcExchange { get; set; }

        public string? DestUri { get; set; }

        public string? DestQueue { get; set; }

        public string? DestExchange { get; set; }

        public string? DestExchangeKey { get; set; }
    }

    private sealed class ShovelParameterDto
    {
        public string? Name { get; set; }

        public ShovelValueDto? Value { get; set; }
    }

    private sealed class ShovelValueDto
    {
        [JsonPropertyName("src-uri")]
        public JsonElement? SrcUri { get; set; }

        [JsonPropertyName("src-queue")]
        public string? SrcQueue { get; set; }

        [JsonPropertyName("src-exchange")]
        public string? SrcExchange { get; set; }

        [JsonPropertyName("dest-uri")]
        public JsonElement? DestUri { get; set; }

        [JsonPropertyName("dest-queue")]
        public string? DestQueue { get; set; }

        [JsonPropertyName("dest-exchange")]
        public string? DestExchange { get; set; }

        [JsonPropertyName("dest-exchange-key")]
        public string? DestExchangeKey { get; set; }

        [JsonPropertyName("ack-mode")]
        public string? AckMode { get; set; }
    }
}
