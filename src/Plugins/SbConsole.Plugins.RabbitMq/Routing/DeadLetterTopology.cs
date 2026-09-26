using SbConsole.Plugins.RabbitMq.Client;

namespace SbConsole.Plugins.RabbitMq.Routing;

/// <summary>
/// Where a queue's dead letters go. RoutingKey null = the message's original routing key is kept.
/// DependsOnMessageRoutingKey = TargetQueues is every queue the DLX *could* route to, not a
/// prediction. SetBy is "argument" or the name of the policy that set the DLX.
/// </summary>
public sealed record DeadLetterRoute(
    string Exchange,
    string? RoutingKey,
    IReadOnlyList<string> TargetQueues,
    bool DependsOnMessageRoutingKey,
    string SetBy);

/// <summary>
/// Dead-letter topology of ONE vhost (design spec §6). Pure. Rules:
/// <list type="bullet">
/// <item>DLX = any queue's DeadLetterExchange (argument or effective policy). The default exchange
/// ("") is never listed in <see cref="DeadLetterExchanges"/> -- it has no row to chip.</item>
/// <item>A DLQ is a queue that is a destination of a binding from a DLX, or that a DLX-"" queue's
/// dead-letter routing key names -- but only if the queue has no DLX of its own. A queue that
/// dead-letters onward is a work/retry queue in a retry loop, not a terminal DLQ.</item>
/// </list>
/// </summary>
public sealed class DeadLetterTopology
{
    private const string DlxArgument = "x-dead-letter-exchange";

    private readonly Dictionary<string, QueueSummary> _queues;
    private readonly IReadOnlyList<BindingInfo> _bindings;
    private readonly Dictionary<string, ExchangeSummary> _exchanges;

    private DeadLetterTopology(
        Dictionary<string, QueueSummary> queues,
        IReadOnlyList<BindingInfo> bindings,
        Dictionary<string, ExchangeSummary> exchanges,
        IReadOnlySet<string> deadLetterExchanges,
        IReadOnlySet<string> deadLetterQueues)
    {
        _queues = queues;
        _bindings = bindings;
        _exchanges = exchanges;
        DeadLetterExchanges = deadLetterExchanges;
        DeadLetterQueues = deadLetterQueues;
    }

    public IReadOnlySet<string> DeadLetterExchanges { get; }

    public IReadOnlySet<string> DeadLetterQueues { get; }

    public bool IsDeadLetterQueue(string queue) => DeadLetterQueues.Contains(queue);

    /// <summary>All inputs must belong to the same vhost.</summary>
    public static DeadLetterTopology Compute(
        IReadOnlyList<QueueSummary> queues,
        IReadOnlyList<BindingInfo> bindings,
        IReadOnlyList<ExchangeSummary> exchanges)
    {
        var byName = new Dictionary<string, QueueSummary>(StringComparer.Ordinal);
        foreach (var q in queues) byName.TryAdd(q.Name, q);

        var exchangesByName = new Dictionary<string, ExchangeSummary>(StringComparer.Ordinal);
        foreach (var e in exchanges) exchangesByName.TryAdd(e.Name, e);

        var dlxs = new HashSet<string>(
            queues.Select(q => q.DeadLetterExchange).OfType<string>().Where(n => n.Length > 0),
            StringComparer.Ordinal);

        bool IsTerminal(string queue) => byName.TryGetValue(queue, out var q) && q.DeadLetterExchange is null;

        var dlqs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dlx in dlxs)
        {
            foreach (var queue in ReachableQueues(dlx, bindings))
            {
                if (IsTerminal(queue)) dlqs.Add(queue);
            }
        }

        foreach (var q in queues)
        {
            if (q.DeadLetterExchange == "" && q.DeadLetterRoutingKey is { } key && IsTerminal(key)) dlqs.Add(key);
        }

        return new DeadLetterTopology(byName, bindings, exchangesByName, dlxs, dlqs);
    }

    /// <summary>
    /// Multi-vhost convenience for the host hooks. BindingInfo and ExchangeSummary carry no vhost,
    /// so the caller supplies them keyed by vhost (e.g. one /api/bindings/{vhost} call per vhost).
    /// One topology per vhost that has queues; a vhost missing from either dictionary is treated as
    /// having no bindings/exchanges.
    /// </summary>
    public static IReadOnlyDictionary<string, DeadLetterTopology> ComputePerVhost(
        IReadOnlyList<QueueSummary> allQueues,
        IReadOnlyDictionary<string, IReadOnlyList<BindingInfo>> bindingsByVhost,
        IReadOnlyDictionary<string, IReadOnlyList<ExchangeSummary>> exchangesByVhost) =>
        allQueues
            .GroupBy(q => q.Vhost, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => Compute(
                    g.ToList(),
                    bindingsByVhost.GetValueOrDefault(g.Key) ?? [],
                    exchangesByVhost.GetValueOrDefault(g.Key) ?? []),
                StringComparer.Ordinal);

    /// <summary>
    /// The queue's dead-letter route, or null when it has none (or isn't in this vhost). With a
    /// dead-letter routing key the targets are resolved through <see cref="RoutingMatcher"/>
    /// (a DLX not in the exchange list is treated as direct). Without one: a fanout DLX reaches
    /// every bound queue regardless; any other DLX lists every queue it's bound to and flags the
    /// result as depending on the message's routing key; the default exchange lists nothing (the
    /// target is whichever queue the original key names). Headers/plugin-type DLXs always list
    /// every bound queue with the flag set, since the key alone doesn't decide.
    /// </summary>
    public DeadLetterRoute? RouteOut(string sourceQueue)
    {
        if (!_queues.TryGetValue(sourceQueue, out var queue) || queue.DeadLetterExchange is not { } dlx) return null;

        var setBy = queue.Arguments.ContainsKey(DlxArgument) ? "argument" : queue.Policy ?? "policy";
        var dlrk = queue.DeadLetterRoutingKey;

        var exchange = dlx.Length == 0
            ? new ExchangeSummary("", "direct", true, false, false, null, null, null, new Dictionary<string, object?>())
            : _exchanges.GetValueOrDefault(dlx)
              ?? new ExchangeSummary(dlx, "direct", true, false, false, null, null, null, new Dictionary<string, object?>());

        var bindings = exchange.IsDefault
            ? RoutingMatcher.SynthesizeDefaultExchangeBindings(_queues.Keys)
            : _bindings.Where(b => string.Equals(b.Source, dlx, StringComparison.Ordinal)).ToList();

        var keyRouted = exchange.IsDefault || exchange.Type is "direct" or "topic";

        if (exchange.Type == "fanout")
        {
            return new DeadLetterRoute(dlx, dlrk, ReachableQueues(dlx, _bindings).ToList(), false, setBy);
        }

        if (keyRouted && dlrk is not null)
        {
            var deep = RoutingMatcher.ResolveDeep(exchange, [.. _exchanges.Values, exchange], exchange.IsDefault ? bindings : _bindings, dlrk, new Dictionary<string, string>());
            return new DeadLetterRoute(dlx, dlrk, deep.Queues.Select(q => q.Queue).ToList(), false, setBy);
        }

        if (exchange.IsDefault)
        {
            return new DeadLetterRoute(dlx, dlrk, [], true, setBy);
        }

        return new DeadLetterRoute(dlx, dlrk, ReachableQueues(dlx, _bindings).ToList(), true, setBy);
    }

    /// <summary>
    /// Every queue a message leaving <paramref name="exchange"/> could reach, ignoring routing keys,
    /// through any number of exchange-to-exchange hops (cycle-safe, <see cref="RoutingMatcher.MaxHops"/> deep).
    /// </summary>
    private static IReadOnlyList<string> ReachableQueues(string exchange, IReadOnlyList<BindingInfo> bindings)
    {
        var queues = new List<string>();
        var seenQueues = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal) { exchange };
        var frontier = new Queue<(string Exchange, int Depth)>();
        frontier.Enqueue((exchange, 0));
        while (frontier.Count > 0)
        {
            var (current, depth) = frontier.Dequeue();
            foreach (var b in bindings.Where(b => string.Equals(b.Source, current, StringComparison.Ordinal)))
            {
                if (b.IsToQueue)
                {
                    if (seenQueues.Add(b.Destination)) queues.Add(b.Destination);
                }
                else if (depth < RoutingMatcher.MaxHops && visited.Add(b.Destination))
                {
                    frontier.Enqueue((b.Destination, depth + 1));
                }
            }
        }

        return queues;
    }

    /// <summary>
    /// The queues whose dead letters can land in <paramref name="deadLetterQueue"/> (their route out
    /// lists it), ordinal-sorted. Empty for a queue that isn't a DLQ.
    /// </summary>
    public IReadOnlyList<string> SourcesOf(string deadLetterQueue) =>
        _queues.Keys
            .Where(q => RouteOut(q) is { } route && route.TargetQueues.Contains(deadLetterQueue, StringComparer.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();
}
