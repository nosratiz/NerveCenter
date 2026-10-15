using System.Text.RegularExpressions;
using SbConsole.Plugins.Kubernetes.Client;

namespace SbConsole.Plugins.Kubernetes.Components;

/// <summary>What <see cref="LogViewer"/> needs to know about one container of the pod.</summary>
/// <param name="CurrentTermination">Set when the container is Terminated now (its Current log ends with this exit).</param>
public sealed record LogContainer(
    string Name, bool IsInit, bool IsCrashLooping, bool HasPrevious,
    K8sTermination? LastTermination, K8sTermination? CurrentTermination, long? MemoryLimitBytes)
{
    public static LogContainer From(K8sContainer c) => new(
        c.Name, c.IsInit,
        IsCrashLooping: c.State == "Waiting" && c.StateReason == "CrashLoopBackOff",
        HasPrevious: c.LastTermination is not null || c.Restarts > 0,
        c.LastTermination,
        c.State == "Terminated" ? c.CurrentTermination : null,
        c.Resources.MemLimitBytes);
}

/// <summary>Pure helpers for rendering log lines (kept out of the .razor file so the regex can be source-generated).</summary>
public static partial class LogText
{
    /// <summary>"k8s-log-error" / "k8s-log-warn" / "k8s-log-info" from the first whole-word level token (any case), else null.</summary>
    public static string? LevelClass(string line)
    {
        var m = LevelPattern().Match(line);
        if (!m.Success)
        {
            return null;
        }

        return char.ToLowerInvariant(m.Value[0]) switch
        {
            'e' => "k8s-log-error",
            'w' => "k8s-log-warn",
            _ => "k8s-log-info",
        };
    }

    /// <summary>Case-insensitive, non-overlapping occurrences of <paramref name="search"/> in <paramref name="line"/>.</summary>
    public static int CountMatches(string line, string search)
    {
        if (search.Length == 0)
        {
            return 0;
        }

        var count = 0;
        var at = 0;
        while ((at = line.IndexOf(search, at, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            at += search.Length;
        }

        return count;
    }

    [GeneratedRegex(@"\b(?:error|warning|warn|info)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LevelPattern();
}
