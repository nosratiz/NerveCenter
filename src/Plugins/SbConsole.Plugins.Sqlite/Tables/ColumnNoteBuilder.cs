using System.Globalization;
using SbConsole.Plugins.Sqlite.Client;

namespace SbConsole.Plugins.Sqlite.Tables;

/// <summary>
/// The Tables page's Notes column (mockup 1b): schema-derived notes are exact, sample-derived ones
/// say "sampled". Values are never altered -- these are hints only.
/// </summary>
public static class ColumnNoteBuilder
{
    public const string UnixMsNote = "unix ms? (sampled)";

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Build(TableDetail detail, IReadOnlyList<ColumnSample>? samples)
    {
        var notes = detail.Columns.ToDictionary(c => c.Name, _ => new List<string>(), StringComparer.Ordinal);

        // INTEGER PRIMARY KEY on a rowid table *is* the rowid (exact declared type, single-column PK).
        var pk = detail.Columns.Where(c => c.PrimaryKeyOrdinal > 0).ToList();
        if (detail.HasRowId && pk is [var only] && string.Equals(only.DeclaredType, "INTEGER", StringComparison.OrdinalIgnoreCase))
        {
            notes[only.Name].Add("alias of rowid");
        }

        foreach (var fk in detail.ForeignKeys)
        {
            if (notes.TryGetValue(fk.FromColumn, out var list))
            {
                list.Add($"→ {fk.ToTable}.{fk.ToColumn ?? "rowid"} · ON DELETE {fk.OnDelete}");
            }
        }

        foreach (var index in detail.Indexes.Where(i => i.Origin != "pk" && i.Columns.Count > 0))
        {
            if (notes.TryGetValue(index.Columns[0], out var list) && !list.Any(n => n.StartsWith("indexed", StringComparison.Ordinal)))
            {
                list.Add($"indexed · {index.Name}");
            }
        }

        foreach (var sample in samples ?? Array.Empty<ColumnSample>())
        {
            if (!notes.TryGetValue(sample.Column, out var list) || sample.NonNullCount == 0)
            {
                continue;
            }

            if (sample.JsonCount > 0)
            {
                var percent = (100.0 * sample.JsonCount / sample.NonNullCount).ToString("0.#", CultureInfo.InvariantCulture);
                list.Add($"JSON in {percent}% of sampled rows");
            }

            if (sample.LooksLikeUnixMs)
            {
                list.Add(UnixMsNote);
            }
            else if (sample.DistinctCount < sample.NonNullCount)
            {
                list.Add($"{SqliteFormat.Count(sample.DistinctCount)} distinct ≈ (sampled)");
            }
        }

        return notes.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, StringComparer.Ordinal);
    }
}
