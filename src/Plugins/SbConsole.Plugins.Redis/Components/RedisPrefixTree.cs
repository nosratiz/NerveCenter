namespace SbConsole.Plugins.Redis.Components;

/// <summary>
/// One node of the Keys browser's prefix tree (design spec §7a). Non-leaf nodes have an empty
/// <see cref="Keys"/>; a node is a leaf for a given key when that key has no further delimiter
/// segment beyond this node's depth (its own full key showed up with nothing left to split).
/// </summary>
public sealed record RedisPrefixNode(string Segment, string FullPrefix, int KeyCount, IReadOnlyList<RedisPrefixNode> Children, IReadOnlyList<string> Keys);

/// <summary>Groups a flat key list into a tree split on a delimiter (default ':') for the Keys browser's left pane.</summary>
public static class RedisPrefixTreeBuilder
{
    public static IReadOnlyList<RedisPrefixNode> Build(IEnumerable<string> keys, char delimiter)
    {
        var root = new MutableNode("", "");
        foreach (var key in keys)
        {
            var segments = key.Split(delimiter);
            var node = root;
            var prefix = "";
            for (var i = 0; i < segments.Length; i++)
            {
                var segment = segments[i];
                prefix = prefix.Length == 0 ? segment : $"{prefix}{delimiter}{segment}";
                if (!node.Children.TryGetValue(segment, out var child))
                {
                    child = new MutableNode(segment, prefix);
                    node.Children[segment] = child;
                }

                child.KeyCount++;
                if (i == segments.Length - 1)
                {
                    child.Keys.Add(key);
                }

                node = child;
            }
        }

        return ToImmutableChildren(root);
    }

    private static IReadOnlyList<RedisPrefixNode> ToImmutableChildren(MutableNode node) =>
        node.Children.Values
            .OrderBy(c => c.Segment, StringComparer.Ordinal)
            .Select(c => new RedisPrefixNode(c.Segment, c.FullPrefix, c.KeyCount, ToImmutableChildren(c), c.Keys))
            .ToList();

    private sealed class MutableNode(string segment, string fullPrefix)
    {
        public string Segment { get; } = segment;
        public string FullPrefix { get; } = fullPrefix;
        public int KeyCount { get; set; }
        public Dictionary<string, MutableNode> Children { get; } = new(StringComparer.Ordinal);
        public List<string> Keys { get; } = [];
    }
}
