using SbConsole.Plugins.Postgres.Client;

namespace SbConsole.Plugins.Postgres.Activity;

public sealed record ActivityRow(PgBackend Backend, int Depth, int BlocksCount, bool IsOwn);

public static class ActivityTree
{
    /// <summary>
    /// Top level: backends whose blockers are not visible, ordered by transaction start (nulls last) then pid; each blocker
    /// is followed by its victims (depth 1, a victim's first visible blocker only), recursively, each backend once.
    /// Cycles: the remaining backends are appended flat at depth 0. Visibility: <paramref name="activeOnly"/> shows only
    /// 'active' backends; otherwise idle backends are hidden unless <paramref name="showIdle"/> is set. HiddenIdle is the
    /// number of 'idle' backends not visible. BlocksCount is the transitive victim count (see TransitiveVictims), counted over all backends, visible or not.
    /// </summary>
    public static (IReadOnlyList<ActivityRow> Rows, int HiddenIdle) Build(PgActivity activity, bool activeOnly, bool showIdle)
    {
        var all = activity.Backends;
        var blocks = all.Select(b => b.Pid).Distinct().ToDictionary(pid => pid, pid => TransitiveVictims(activity, pid));

        var visible = all.Where(b => activeOnly ? b.State == "active" : (showIdle || b.State != "idle")).ToList();
        var hidden = all.Count(b => b.State == "idle") - visible.Count(b => b.State == "idle");
        var present = visible.Select(b => b.Pid).ToHashSet();

        int? FirstBlocker(PgBackend b) => b.BlockedBy.Cast<int?>().FirstOrDefault(p => present.Contains(p!.Value));

        var ordered = visible.OrderBy(b => b.XactStart is null).ThenBy(b => b.XactStart).ThenBy(b => b.Pid).ToList();
        var children = ordered.Where(b => FirstBlocker(b) is not null).GroupBy(b => FirstBlocker(b)!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var rows = new List<ActivityRow>();
        var seen = new HashSet<int>();

        void Add(PgBackend b, int depth, bool recurse = true)
        {
            if (!seen.Add(b.Pid))
            {
                return;
            }

            rows.Add(new ActivityRow(b, depth, blocks.GetValueOrDefault(b.Pid), b.Pid == activity.OwnPid));
            if (recurse && children.TryGetValue(b.Pid, out var kids))
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

        // Anything left is in a blocking cycle: flat, no recursion.
        foreach (var b in ordered)
        {
            Add(b, 0, recurse: false);
        }

        return (rows, hidden);
    }

    /// <summary>
    /// Distinct backends waiting on <paramref name="pid"/> directly or through a chain of waiters. Cycle-safe; the backend
    /// itself is never counted.
    /// </summary>
    public static int TransitiveVictims(PgActivity activity, int pid)
    {
        var victimsOf = new Dictionary<int, List<int>>();
        foreach (var b in activity.Backends)
        {
            foreach (var blocker in b.BlockedBy.Distinct())
            {
                if (!victimsOf.TryGetValue(blocker, out var list))
                {
                    victimsOf[blocker] = list = [];
                }

                list.Add(b.Pid);
            }
        }

        var seen = new HashSet<int> { pid };
        var queue = new Queue<int>();
        queue.Enqueue(pid);
        while (queue.Count > 0)
        {
            if (victimsOf.TryGetValue(queue.Dequeue(), out var next))
            {
                foreach (var victim in next)
                {
                    if (seen.Add(victim))
                    {
                        queue.Enqueue(victim);
                    }
                }
            }
        }

        return seen.Count - 1;
    }

    /// <summary>
    /// The row blocking the most sessions; ties go to the longest duration. Computed over all backends, independent of the
    /// view filter, so the banner still names an idle-in-transaction blocker when Active only is on.
    /// </summary>
    public static ActivityRow? WorstBlocker(PgActivity activity) =>
        Build(activity, false, true).Rows.Where(r => r.BlocksCount > 0)
            .OrderByDescending(r => r.BlocksCount).ThenByDescending(r => r.Backend.Duration ?? TimeSpan.Zero)
            .FirstOrDefault();
}
