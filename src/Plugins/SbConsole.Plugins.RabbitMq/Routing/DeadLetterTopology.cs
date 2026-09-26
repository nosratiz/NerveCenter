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
    /// The queue's dead-letter route, or null when it has none (or isn't in this vhost). The default
    /// exchange is unchanged: with a dead-letter routing key it names the target queue directly (via
    /// <see cref="RoutingMatcher"/> over the synthesized default-exchange bindings), flag false;
    /// without one the target is whichever queue the original key names, flag true. For a real DLX
    /// (a DLX not in the exchange list is treated as direct): when the queue has a dead-letter
    /// routing key AND every exchange reachable from the DLX through exchange-to-exchange bindings
    /// (BFS, <see cref="RoutingMatcher.MaxHops"/> deep) is direct/topic/fanout -- "key-deterministic"
    /// -- the targets are resolved through <see cref="RoutingMatcher.ResolveDeep"/> with that key,
    /// flag false; this now covers a fanout DLX too, since fanout matches any key. Without a routing
    /// key, a purely-fanout topology still reaches every bound queue regardless of key, flag false.
    /// Everything else -- no routing key with any key-routed hop, a headers/plugin-typed/unknown hop
    /// anywhere in the topology, or the hop limit being hit -- lists every reachable queue and flags
    /// the result as depending on the message's routing key, since it can't be predicted here. A
    /// reachable exchange missing from the exchange list (other than the DLX itself, which falls
    /// back to direct) is a dead end -- and so doesn't count against determinism -- only when it has
    /// no outgoing bindings of its own; one that does is unknown and can route anywhere, so it always
    /// counts as unpredictable.
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

        if (exchange.IsDefault)
        {
            if (dlrk is not null)
            {
                var bindings = RoutingMatcher.SynthesizeDefaultExchangeBindings(_queues.Keys);
                var deep = RoutingMatcher.ResolveDeep(exchange, [.. _exchanges.Values, exchange], bindings, dlrk, new Dictionary<string, string>());
                return new DeadLetterRoute(dlx, dlrk, deep.Queues.Select(q => q.Queue).ToList(), false, setBy);
            }

            return new DeadLetterRoute(dlx, dlrk, [], true, setBy);
        }

        var (reachableQueues, reachableExchanges, reachedHopLimit) = ReachableTopology(dlx, _bindings);

        string? TypeOf(string name) =>
            string.Equals(name, dlx, StringComparison.Ordinal) ? exchange.Type : _exchanges.GetValueOrDefault(name)?.Type;

        bool HasOutgoingBindings(string name) => _bindings.Any(b => string.Equals(b.Source, name, StringComparison.Ordinal));

        static bool IsKeyRoutedType(string? type) => type is "direct" or "topic" or "fanout";

        bool IsDeadEnd(string name) => TypeOf(name) is null && !HasOutgoingBindings(name);

        var keyDeterministic = !reachedHopLimit
            && reachableExchanges.All(name => IsDeadEnd(name) || IsKeyRoutedType(TypeOf(name)));

        if (dlrk is not null && keyDeterministic)
        {
            var deep = RoutingMatcher.ResolveDeep(exchange, [.. _exchanges.Values, exchange], _bindings, dlrk, new Dictionary<string, string>());
            return new DeadLetterRoute(dlx, dlrk, deep.Queues.Select(q => q.Queue).ToList(), false, setBy);
        }

        var allFanout = !reachedHopLimit && reachableExchanges.All(name => TypeOf(name) == "fanout");

        if (dlrk is null && allFanout)
        {
            return new DeadLetterRoute(dlx, dlrk, reachableQueues.ToList(), false, setBy);
        }

        return new DeadLetterRoute(dlx, dlrk, reachableQueues.ToList(), true, setBy);
    }

    /// <summary>
    /// Every queue a message leaving <paramref name="exchange"/> could reach, ignoring routing keys,
    /// and every exchange (including <paramref name="exchange"/> itself) it passes through on the
    /// way, through any number of exchange-to-exchange hops (cycle-safe, <see cref="RoutingMatcher.MaxHops"/>
    /// deep). <c>ReachedHopLimit</c> is true when a hop was refused only because the depth limit was
    /// reached (not merely because it revisits an already-explored exchange).
    /// </summary>
    private static (IReadOnlyList<string> Queues, IReadOnlyList<string> Exchanges, bool ReachedHopLimit) ReachableTopology(
        string exchange, IReadOnlyList<BindingInfo> bindings)
    {
        var queues = new List<string>();
        var seenQueues = new HashSet<string>(StringComparer.Ordinal);
        var exchanges = new List<string> { exchange };
        var visited = new HashSet<string>(StringComparer.Ordinal) { exchange };
        var frontier = new Queue<(string Exchange, int Depth)>();
        frontier.Enqueue((exchange, 0));
        var reachedHopLimit = false;

        while (frontier.Count > 0)
        {
            var (current, depth) = frontier.Dequeue();
            foreach (var b in bindings.Where(b => string.Equals(b.Source, current, StringComparison.Ordinal)))
            {
                if (b.IsToQueue)
                {
                    if (seenQueues.Add(b.Destination)) queues.Add(b.Destination);
                }
                else if (depth < RoutingMatcher.MaxHops)
                {
                    if (visited.Add(b.Destination))
                    {
                        exchanges.Add(b.Destination);
                        frontier.Enqueue((b.Destination, depth + 1));
                    }
                }
                else if (!visited.Contains(b.Destination))
                {
                    reachedHopLimit = true;
                }
            }
        }

        return (queues, exchanges, reachedHopLimit);
    }

    /// <summary>
    /// Every queue a message leaving <paramref name="exchange"/> could reach, ignoring routing keys,
    /// through any number of exchange-to-exchange hops.
    /// </summary>
    private static IReadOnlyList<string> ReachableQueues(string exchange, IReadOnlyList<BindingInfo> bindings) =>
        ReachableTopology(exchange, bindings).Queues;

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
