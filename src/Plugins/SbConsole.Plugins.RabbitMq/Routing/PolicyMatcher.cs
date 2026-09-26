using System.Text.RegularExpressions;
using SbConsole.Plugins.RabbitMq.Client;

namespace SbConsole.Plugins.RabbitMq.Routing;

/// <summary>
/// Client-side policy pattern evaluation (1h Matches column, queue detail's effective policy).
/// Patterns run on the .NET engine with a 100 ms match timeout; a pattern .NET rejects or that
/// times out yields null ("?" in the UI) for counts and is skipped when picking a winner.
/// </summary>
public static class PolicyMatcher
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// How many objects the policy's pattern matches, per its apply-to: queues (all types),
    /// classic_queues/quorum_queues/streams (that queue type), exchanges (excluding the default
    /// "" exchange), all (both). Null for an invalid/timed-out pattern or an unknown apply-to.
    /// </summary>
    public static int? CountMatches(PolicyInfo policy, IEnumerable<QueueSummary> queues, IEnumerable<ExchangeSummary> exchanges)
    {
        var regex = TryCreate(policy.Pattern);
        if (regex is null) return null;

        IEnumerable<string>? names = policy.ApplyTo switch
        {
            "queues" => queues.Select(q => q.Name),
            "classic_queues" or "quorum_queues" or "streams" =>
                queues.Where(q => AppliesToQueueType(policy.ApplyTo, q.Type)).Select(q => q.Name),
            "exchanges" => ExchangeNames(exchanges),
            "all" => queues.Select(q => q.Name).Concat(ExchangeNames(exchanges)),
            _ => null,
        };

        if (names is null) return null;

        try
        {
            return names.Count(regex.IsMatch);
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// The queue-applicable policy RabbitMQ would apply: highest Priority among policies whose
    /// apply-to covers this queue and whose pattern matches its name. Ties (which RabbitMQ leaves
    /// unspecified) break by policy name, ordinal ascending, so the answer is deterministic.
    /// </summary>
    public static PolicyInfo? WinningPolicy(QueueSummary queue, IEnumerable<PolicyInfo> policies) =>
        policies
            .Where(p => p.ApplyTo is "queues" or "all" || AppliesToQueueType(p.ApplyTo, queue.Type))
            .Where(p => SafeIsMatch(p.Pattern, queue.Name))
            .OrderByDescending(p => p.Priority)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .FirstOrDefault();

    private static bool AppliesToQueueType(string applyTo, string queueType) => (applyTo, queueType) switch
    {
        ("classic_queues", "classic") => true,
        ("quorum_queues", "quorum") => true,
        ("streams", "stream") => true,
        _ => false,
    };

    private static IEnumerable<string> ExchangeNames(IEnumerable<ExchangeSummary> exchanges) =>
        exchanges.Where(e => !e.IsDefault).Select(e => e.Name);

    private static bool SafeIsMatch(string pattern, string input)
    {
        var regex = TryCreate(pattern);
        if (regex is null) return false;

        try
        {
            return regex.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static Regex? TryCreate(string pattern)
    {
        try
        {
            return new Regex(pattern, RegexOptions.CultureInvariant, MatchTimeout);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
