using System.Globalization;
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
    bool IsUnknownType);

/// <summary>
/// Predicts which bindings of an exchange a message would match (design spec §5). Pure.
/// Exchange-to-exchange bindings are matched like any other and not followed.
/// </summary>
public static class RoutingMatcher
{
    private const int MaxClosest = 3;

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

        return new RoutePreview(matched, closest, hasAlternate, IsUnknownType: false);
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
        var mode = bindingArgs.TryGetValue("x-match", out var m) ? AsString(m) : "all";
        var (any, withX) = mode switch
        {
            "any" => (true, false),
            "any-with-x" => (true, true),
            "all-with-x" => (false, true),
            _ => (false, false),
        };

        var considered = bindingArgs
            .Where(a => a.Key != "x-match" && (withX || !a.Key.StartsWith("x-", StringComparison.Ordinal)))
            .ToList();

        bool Satisfied(KeyValuePair<string, object?> arg) =>
            headers.TryGetValue(arg.Key, out var value)
            // A void (null) binding value matches on presence alone, as in RabbitMQ.
            && (arg.Value is null || string.Equals(AsString(arg.Value), value, StringComparison.Ordinal));

        // Matches RabbitMQ: "all" over no arguments matches everything, "any" over none matches nothing.
        return any ? considered.Any(Satisfied) : considered.All(Satisfied);
    }

    private static string? AsString(object? value) => value switch
    {
        null => null,
        string s => s,
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

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
