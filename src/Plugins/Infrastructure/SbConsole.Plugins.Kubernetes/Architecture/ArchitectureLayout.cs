using System.Globalization;

namespace SbConsole.Plugins.Kubernetes.Architecture;

public enum ArchGroupBy { Namespace, Team, None }

public sealed record ArchLayoutOptions(ArchGroupBy GroupBy, bool ShowStores, bool ShowExternal);

public sealed record PlacedNode(ArchNode Node, int X, int Y, int Width);

/// <summary>A background band: a namespace/team lane, or (<see cref="Side"/>) the stores · brokers · external column.</summary>
public sealed record ArchLane(string Label, int X, int Y, int Width, int Height, bool Side);

/// <summary><see cref="Path"/> is an SVG cubic from the source's right edge to the target's left edge.</summary>
public sealed record PlacedEdge(ArchEdge Edge, string Path, int LabelX, int LabelY);

public sealed record ArchLayout(IReadOnlyList<PlacedNode> Nodes, IReadOnlyList<ArchLane> Lanes, IReadOnlyList<PlacedEdge> Edges, int Width, int Height)
{
    public static readonly ArchLayout Empty = new([], [], [], 0, 0);
}

/// <summary>
/// Places the map (mockup 4a): ingresses and selected workloads left to right by dependency depth (columns shared by all
/// lanes, so depth reads across), one lane per namespace or team; stores, unbacked Services, dependencies from other
/// namespaces and external hosts stacked in a column on the right. Back edges of cycles are ignored for depth. Pure.
/// </summary>
public static class ArchitectureLayout
{
    public const int NodeWidth = 160;
    public const int SideNodeWidth = 180;
    public const int NodeHeight = 52;
    public const string SideLabel = "stores · brokers · external";
    public const string NoTeam = "no team";

    private const int ColumnStep = 210;
    private const int RowStep = 70;
    private const int LanePadX = 12;
    private const int LanePadTop = 30;
    private const int LanePadBottom = 12;
    private const int LaneGap = 10;
    private const int SideGap = 46;

    public static ArchLayout Compute(ArchitectureGraph graph, ArchLayoutOptions options)
    {
        var visible = graph.Nodes.Where(n => n.Kind switch
        {
            ArchNodeKind.Store or ArchNodeKind.Service => options.ShowStores,
            ArchNodeKind.External => options.ShowExternal,
            _ => true,
        }).ToList();
        if (visible.Count == 0)
        {
            return ArchLayout.Empty;
        }

        var visibleIds = visible.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var edges = graph.Edges.Where(e => visibleIds.Contains(e.From) && visibleIds.Contains(e.To)).ToList();
        var main = visible.Where(IsMain).OrderBy(n => n.Name, StringComparer.Ordinal).ThenBy(n => n.Id, StringComparer.Ordinal).ToList();
        var side = visible.Where(n => !IsMain(n))
            .OrderBy(SideRank).ThenBy(n => n.Name, StringComparer.Ordinal).ThenBy(n => n.Id, StringComparer.Ordinal).ToList();

        var depth = Depths(main, edges);
        var columns = main.Count == 0 ? 0 : depth.Values.Max() + 1;
        var laneWidth = columns == 0 ? 0 : (columns - 1) * ColumnStep + NodeWidth + 2 * LanePadX;

        var placed = new List<PlacedNode>(visible.Count);
        var lanes = new List<ArchLane>();
        var y = 0;
        foreach (var lane in main.GroupBy(n => LaneKey(n, options.GroupBy)).OrderBy(g => LaneOrder(g.Key, options.GroupBy)).ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            var rows = 0;
            foreach (var column in lane.GroupBy(n => depth[n.Id]))
            {
                var row = 0;
                foreach (var node in column)
                {
                    placed.Add(new PlacedNode(node, LanePadX + column.Key * ColumnStep, y + LanePadTop + row * RowStep, NodeWidth));
                    row++;
                }

                rows = Math.Max(rows, row);
            }

            var height = LanePadTop + (rows - 1) * RowStep + NodeHeight + LanePadBottom;
            lanes.Add(new ArchLane(lane.Key, 0, y, laneWidth, height, false));
            y += height + LaneGap;
        }

        var sideX = laneWidth == 0 ? 0 : laneWidth + SideGap;
        if (side.Count > 0)
        {
            for (var i = 0; i < side.Count; i++)
            {
                placed.Add(new PlacedNode(side[i], sideX + LanePadX, LanePadTop + i * RowStep, SideNodeWidth));
            }

            var sideHeight = LanePadTop + (side.Count - 1) * RowStep + NodeHeight + LanePadBottom;
            lanes.Add(new ArchLane(SideLabel, sideX, 0, SideNodeWidth + 2 * LanePadX, sideHeight, true));
        }

        var byId = placed.ToDictionary(p => p.Node.Id, StringComparer.Ordinal);
        var placedEdges = edges.Select(e => Route(e, byId[e.From], byId[e.To])).ToList();
        var width = lanes.Max(l => l.X + l.Width);
        var heightTotal = lanes.Max(l => l.Y + l.Height);
        return new ArchLayout(placed, lanes, placedEdges, width, heightTotal);
    }

    private static bool IsMain(ArchNode n) => n.InScope && n.Kind is ArchNodeKind.Ingress or ArchNodeKind.Workload;

    private static int SideRank(ArchNode n) => n.Kind switch
    {
        ArchNodeKind.Store => 0,
        ArchNodeKind.Service => 1,
        ArchNodeKind.Workload => 2,
        _ => 3,
    };

    private static string LaneKey(ArchNode n, ArchGroupBy by) => by switch
    {
        ArchGroupBy.Namespace => n.Namespace ?? "",
        ArchGroupBy.Team => n.Team ?? NoTeam,
        _ => "",
    };

    private static int LaneOrder(string key, ArchGroupBy by) => by == ArchGroupBy.Team && key == NoTeam ? 1 : 0;

    /// <summary>Longest path from a root over the main nodes' edges, after dropping the back edges a DFS finds.</summary>
    private static Dictionary<string, int> Depths(IReadOnlyList<ArchNode> main, IReadOnlyList<ArchEdge> edges)
    {
        var ids = main.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var successors = main.ToDictionary(n => n.Id, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var e in edges.Where(e => ids.Contains(e.From) && ids.Contains(e.To)))
        {
            successors[e.From].Add(e.To);
        }

        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>(main.Count);
        var dag = new List<(string From, string To)>();

        void Visit(string start)
        {
            // Iterative DFS: (node, next successor index).
            var stack = new Stack<(string Id, int Next)>();
            stack.Push((start, 0));
            state[start] = 1;
            while (stack.Count > 0)
            {
                var (id, next) = stack.Pop();
                var succ = successors[id];
                if (next < succ.Count)
                {
                    stack.Push((id, next + 1));
                    var to = succ[next];
                    var s = state.GetValueOrDefault(to);
                    if (s == 1)
                    {
                        continue; // back edge
                    }

                    dag.Add((id, to));
                    if (s == 0)
                    {
                        state[to] = 1;
                        stack.Push((to, 0));
                    }
                }
                else
                {
                    state[id] = 2;
                    order.Add(id);
                }
            }
        }

        foreach (var n in main)
        {
            if (state.GetValueOrDefault(n.Id) == 0)
            {
                Visit(n.Id);
            }
        }

        var depth = main.ToDictionary(n => n.Id, _ => 0, StringComparer.Ordinal);
        var incoming = dag.ToLookup(e => e.To, e => e.From, StringComparer.Ordinal);
        order.Reverse(); // reverse postorder is a topological order of the DAG
        foreach (var id in order)
        {
            foreach (var from in incoming[id])
            {
                depth[id] = Math.Max(depth[id], depth[from] + 1);
            }
        }

        return depth;
    }

    private static PlacedEdge Route(ArchEdge edge, PlacedNode from, PlacedNode to)
    {
        var x1 = from.X + from.Width;
        var y1 = from.Y + NodeHeight / 2;
        var x2 = to.X;
        var y2 = to.Y + NodeHeight / 2;
        var (c1, c2) = x2 > x1 ? ((x1 + x2) / 2, (x1 + x2) / 2) : (x1 + 40, x2 - 40);
        var path = string.Create(CultureInfo.InvariantCulture, $"M{x1} {y1} C{c1} {y1} {c2} {y2} {x2} {y2}");
        return new PlacedEdge(edge, path, (x1 + x2) / 2, (y1 + y2) / 2);
    }
}
