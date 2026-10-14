using SbConsole.Plugins.Postgres.Client;

namespace SbConsole.Plugins.Postgres.Activity;

public sealed record ActivityRow(PgBackend Backend, int Depth, int BlocksCount, bool IsOwn);

public static class ActivityTree
{
    /// <summary>
    /// Top level: backends with no blockers, ordered by transaction start (nulls last) then pid; each blocker is followed by
    /// its victims (depth 1, a victim's first blocker only), recursively, each backend once. Cycles: the remaining backends
    /// are appended at depth 0. Idle rows are hidden when <paramref name="activeOnly"/> is set and <paramref name="showIdle"/>
    /// is not; the hidden count is returned separately.
    /// </summary>
    public static (IReadOnlyList<ActivityRow> Rows, int HiddenIdle) Build(PgActivity activity, bool activeOnly, bool showIdle)
    {
        var hide = activeOnly && !showIdle;
        var all = activity.Backends;
        var blocks = new Dictionary<int, int>();
        foreach (var b in all)
        {
            foreach (var blocker in b.BlockedBy.Distinct())
            {
                blocks[blocker] = blocks.GetValueOrDefault(blocker) + 1;
            }
        }

        var visible = all.Where(b => !(hide && b.State == "idle")).ToList();
        var hidden = all.Count - visible.Count;
        var present = visible.Select(b => b.Pid).ToHashSet();

        int? FirstBlocker(PgBackend b) => b.BlockedBy.Cast<int?>().FirstOrDefault(p => present.Contains(p!.Value));

        var ordered = visible.OrderBy(b => b.XactStart is null).ThenBy(b => b.XactStart).ThenBy(b => b.Pid).ToList();
        var children = ordered.Where(b => FirstBlocker(b) is not null).GroupBy(b => FirstBlocker(b)!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var rows = new List<ActivityRow>();
        var seen = new HashSet<int>();

        void Add(PgBackend b, int depth)
        {
            if (!seen.Add(b.Pid))
            {
                return;
            }

            rows.Add(new ActivityRow(b, depth, blocks.GetValueOrDefault(b.Pid), b.Pid == activity.OwnPid));
            if (children.TryGetValue(b.Pid, out var kids))
            {
                foreach (var kid in kids)
                {
                    Add(kid, depth + 1);
                }
            }
        }

        foreach (var b in ordered.Where(b => FirstBlocker(b) is null))
        {
            Add(b, 0);
        }

        // Anything left is in a blocking cycle.
        foreach (var b in ordered)
        {
            Add(b, 0);
        }

        return (rows, hidden);
    }

    /// <summary>The row blocking the most sessions; ties go to the longest duration.</summary>
    public static ActivityRow? WorstBlocker(IReadOnlyList<ActivityRow> rows) =>
        rows.Where(r => r.BlocksCount > 0)
            .OrderByDescending(r => r.BlocksCount).ThenByDescending(r => r.Backend.Duration ?? TimeSpan.Zero)
            .FirstOrDefault();
}
