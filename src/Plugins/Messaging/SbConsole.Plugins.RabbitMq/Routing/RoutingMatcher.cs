using SbConsole.Plugins.RabbitMq.Client;

namespace SbConsole.Plugins.RabbitMq.Routing;

/// <summary>
/// The routing preview's result. <see cref="Closest"/> is only populated when nothing matched and
/// the exchange routes by key (direct/topic/default). <see cref="IsUnknownType"/> marks a plugin
/// exchange type (x-consistent-hash, x-delayed-message, ...) whose routing can't be predicted.
/// </summary>
public sealed record RoutePreview(
    IReadOnlyList<BindingInfo> Matched,
    IReadOnlyList<BindingInfo> Closest,
    bool HasAlternateExchange,
    bool IsUnknownType)
{
    /// <summary>
    /// Headers exchanges only: the (sorted, distinct) binding argument keys whose value is typed
    /// (number, bool, ...). The broker compares header values with their types, and the console
    /// publishes every header as a string, so those arguments can never be satisfied from here.
    /// </summary>
    public IReadOnlyList<string> TypedHeaderArguments { get; init; } = [];
}

/// <summary>A queue a message would reach, and the exchanges it passes through on the way (first = the publish exchange).</summary>
public sealed record RoutedQueue(string Queue, IReadOnlyList<string> Path, BindingInfo Binding);

/// <summary>
/// <see cref="Root"/> is the single-hop preview of the publish exchange (drives the blocked/closest
/// UI); <see cref="Queues"/> is every queue reached through any number of exchange hops.
/// </summary>
public sealed record DeepRoutePreview(
    RoutePreview Root,
    IReadOnlyList<RoutedQueue> Queues,
    bool ReachedHopLimit,
    IReadOnlyList<string> UnpredictableExchanges);

/// <summary>
/// Predicts which bindings of an exchange a message would match (design spec §5). Pure.
/// Resolve matches one exchange's bindings; ResolveDeep follows exchange-to-exchange bindings.
/// </summary>
public static class RoutingMatcher
{
    private const int MaxClosest = 3;

    /// <summary>Exchange hops followed before giving up (the broker has no limit; a preview needs one).</summary>
    public const int MaxHops = 8;

    public static string ExchangeLabel(ExchangeSummary exchange) => exchange.IsDefault ? "(AMQP default)" : exchange.Name;

    public static RoutePreview Resolve(
        ExchangeSummary exchange,
        IReadOnlyList<BindingInfo> sourceBindings,
        string routingKey,
        IReadOnlyDictionary<string, string> headers)
    {
        var hasAlternate = !string.IsNullOrEmpty(exchange.AlternateExchange);
        var type = exchange.IsDefault ? "direct" : exchange.Type;

        Func<BindingInfo, bool>? predicate = type switch
        {
            "direct" => b => string.Equals(b.RoutingKey, routingKey, StringComparison.Ordinal),
            "fanout" => _ => true,
            "topic" => b => TopicMatches(b.RoutingKey, routingKey),
            "headers" => b => HeadersMatch(b.Arguments, headers),
            _ => null,
        };

        if (predicate is null)
        {
            return new RoutePreview([], [], hasAlternate, IsUnknownType: true);
        }

        var matched = sourceBindings.Where(predicate).ToList();
        var closest = matched.Count == 0 && type is ("direct" or "topic")
            ? RankClosest(sourceBindings, routingKey)
            : [];

        return new RoutePreview(matched, closest, hasAlternate, IsUnknownType: false)
        {
            TypedHeaderArguments = type == "headers" ? TypedArguments(sourceBindings) : [],
        };
    }

    /// <summary>
    /// Follows exchange-to-exchange bindings breadth-first with the same routing key and headers,
    /// evaluating each exchange at most once (a cycle is not an error -- the broker also delivers
    /// one copy per queue). A hop that matches nothing falls through to its alternate exchange.
    /// A plugin-typed hop can't be predicted and is reported, not followed. Queues keep the first
    /// (shortest) path that reached them.
    /// </summary>
    public static DeepRoutePreview ResolveDeep(
        ExchangeSummary root,
        IReadOnlyList<ExchangeSummary> exchanges,
        IReadOnlyList<BindingInfo> vhostBindings,
        string routingKey,
        IReadOnlyDictionary<string, string> headers)
    {
        var byName = new Dictionary<string, ExchangeSummary>(StringComparer.Ordinal);
        foreach (var e in exchanges) byName.TryAdd(e.Name, e);

        IReadOnlyList<BindingInfo> BindingsOf(ExchangeSummary e) =>
            vhostBindings.Where(b => string.Equals(b.Source, e.Name, StringComparison.Ordinal)).ToList();

        var rootPreview = Resolve(root, BindingsOf(root), routingKey, headers);
        var queues = new List<RoutedQueue>();
        var seenQueues = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal) { root.Name };
        var unpredictable = new List<string>();
        var reachedLimit = false;

        var frontier = new Queue<(ExchangeSummary Exchange, RoutePreview Preview, List<string> Path)>();
        frontier.Enqueue((root, rootPreview, [ExchangeLabel(root)]));

        while (frontier.Count > 0)
        {
            var (exchange, preview, path) = frontier.Dequeue();
            if (preview.IsUnknownType)
            {
                if (!ReferenceEquals(exchange, root)) unpredictable.Add(exchange.Name);
                continue;
            }

            void Follow(string next)
            {
                if (!byName.TryGetValue(next, out var nextExchange) || !visited.Add(next)) return;
                if (path.Count > MaxHops)
                {
                    reachedLimit = true;
                    return;
                }

                frontier.Enqueue((nextExchange, Resolve(nextExchange, BindingsOf(nextExchange), routingKey, headers), [.. path, next]));
            }

            if (preview.Matched.Count == 0 && !string.IsNullOrEmpty(exchange.AlternateExchange))
            {
                Follow(exchange.AlternateExchange);
                continue;
            }

            foreach (var binding in preview.Matched)
            {
                if (binding.IsToQueue)
                {
                    if (seenQueues.Add(binding.Destination))
                    {
                        queues.Add(new RoutedQueue(binding.Destination, [.. path, binding.Destination], binding));
                    }
                }
                else
                {
                    Follow(binding.Destination);
                }
            }
        }

        return new DeepRoutePreview(rootPreview, queues, reachedLimit, unpredictable);
    }

    /// <summary>
    /// The default exchange's implicit bindings: every queue is bound with its own name as key.
    /// </summary>
    public static IReadOnlyList<BindingInfo> SynthesizeDefaultExchangeBindings(IEnumerable<string> queueNames) =>
        queueNames
            .Select(name => new BindingInfo("", name, "queue", name, new Dictionary<string, object?>(), name))
            .ToList();

    /// <summary>
    /// AMQP topic match. Both sides split on '.'; '*' is exactly one word, '#' zero or more words.
    /// As in RabbitMQ, an empty string is zero words (so "#" matches "" and "*" doesn't), while an
    /// empty word between dots ("a..b", "a.") is a real, empty word ('*' matches it).
    /// </summary>
    public static bool TopicMatches(string pattern, string routingKey)
    {
        var p = SplitWords(pattern);
        var k = SplitWords(routingKey);

        // can[i, j]: pattern words from i match key words from j. Filled back to front.
        var can = new bool[p.Length + 1, k.Length + 1];
        can[p.Length, k.Length] = true;

        for (var i = p.Length - 1; i >= 0; i--)
        {
            for (var j = k.Length; j >= 0; j--)
            {
                can[i, j] = p[i] switch
                {
                    // Zero words, or consume one key word and stay on '#'.
                    "#" => can[i + 1, j] || (j < k.Length && can[i, j + 1]),
                    "*" => j < k.Length && can[i + 1, j + 1],
                    var word => j < k.Length && string.Equals(word, k[j], StringComparison.Ordinal) && can[i + 1, j + 1],
                };
            }
        }

        return can[0, 0];
    }

    private static string[] SplitWords(string key) => key.Length == 0 ? [] : key.Split('.');

    // ------------------------------------------------------------------ headers

    private static bool HeadersMatch(IReadOnlyDictionary<string, object?> bindingArgs, IReadOnlyDictionary<string, string> headers)
    {
        var any = MatchMode(bindingArgs).Any;

        bool Satisfied(KeyValuePair<string, object?> arg) =>
            headers.TryGetValue(arg.Key, out var value)
            && arg.Value switch
            {
                // A void (null) binding value matches on presence alone, as in RabbitMQ.
                null => true,
                string expected => string.Equals(expected, value, StringComparison.Ordinal),
                // Typed (number/bool/...) never equals a string header: the broker compares types.
                _ => false,
            };

        // Matches RabbitMQ: "all" over no arguments matches everything, "any" over none matches nothing.
        var considered = ConsideredArguments(bindingArgs);
        return any ? considered.Any(Satisfied) : considered.All(Satisfied);
    }

    private static (bool Any, bool WithX) MatchMode(IReadOnlyDictionary<string, object?> bindingArgs) =>
        (bindingArgs.TryGetValue("x-match", out var m) ? m as string : null) switch
        {
            "any" => (true, false),
            "any-with-x" => (true, true),
            "all-with-x" => (false, true),
            _ => (false, false),
        };

    private static List<KeyValuePair<string, object?>> ConsideredArguments(IReadOnlyDictionary<string, object?> bindingArgs)
    {
        var withX = MatchMode(bindingArgs).WithX;
        return bindingArgs
            .Where(a => a.Key != "x-match" && (withX || !a.Key.StartsWith("x-", StringComparison.Ordinal)))
            .ToList();
    }

    private static List<string> TypedArguments(IEnumerable<BindingInfo> bindings) =>
        bindings
            .SelectMany(b => ConsideredArguments(b.Arguments))
            .Where(a => a.Value is not null and not string)
            .Select(a => a.Key)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    // ------------------------------------------------------------------ closest

    private static List<BindingInfo> RankClosest(IReadOnlyList<BindingInfo> bindings, string routingKey)
    {
        var keyWords = SplitWords(routingKey);

        return bindings
            .GroupBy(b => b.RoutingKey, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderByDescending(b => SharedLeadingWords(SplitWords(b.RoutingKey), keyWords))
            .ThenBy(b => Levenshtein.Distance(b.RoutingKey, routingKey))
            .ThenBy(b => b.RoutingKey, StringComparer.Ordinal)
            .Take(MaxClosest)
            .ToList();
    }

    // Leading words in common; '*' in the binding counts as agreeing with any word, '#' stops the count.
    private static int SharedLeadingWords(string[] bindingWords, string[] keyWords)
    {
        var n = 0;
        while (n < bindingWords.Length && n < keyWords.Length
               && bindingWords[n] != "#"
               && (bindingWords[n] == "*" || string.Equals(bindingWords[n], keyWords[n], StringComparison.Ordinal)))
        {
            n++;
        }

        return n;
    }
}
