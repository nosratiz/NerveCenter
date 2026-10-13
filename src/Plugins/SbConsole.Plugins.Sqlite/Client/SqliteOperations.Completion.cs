using Microsoft.Data.Sqlite;

namespace SbConsole.Plugins.Sqlite.Client;

/// <summary>Query IntelliSense (design spec §12): tables, views and their columns, from the live file or a backup.</summary>
public sealed partial class SqliteOperations
{
    public Task<CompletionSchema> GetCompletionSchemaAsync(string secret, string? backupFile = null, CancellationToken ct = default) =>
        RunAsync(secret, QueryOptions(backupFile), c =>
        {
            // One extra row tells "exactly the cap" from "more than the cap".
            var objects = SqliteSql.Rows(c,
                @"SELECT name, type FROM sqlite_schema WHERE type IN ('table', 'view') AND name NOT LIKE 'sqlite\_%' ESCAPE '\' ORDER BY name LIMIT $limit",
                r => (Name: r.GetString(0), Type: r.GetString(1) == "view" ? SchemaObjectType.View : SchemaObjectType.Table),
                ("$limit", CompletionSchema.MaxObjects + 1));
            var capped = objects.Count > CompletionSchema.MaxObjects;
            var result = new List<CompletionObject>();
            foreach (var (name, type) in objects.Take(CompletionSchema.MaxObjects))
            {
                ct.ThrowIfCancellationRequested();
                var columns = ReadCompletionColumns(c, name);
                capped |= columns.Count > CompletionSchema.MaxColumns;
                result.Add(new CompletionObject(name, type, columns.Take(CompletionSchema.MaxColumns).ToList()));
            }

            return new CompletionSchema(result, capped);
        }, ct);

    private static List<CompletionColumn> ReadCompletionColumns(SqliteConnection c, string name)
    {
        try
        {
            return SqliteSql.Rows(c, "SELECT name, type, pk FROM pragma_table_xinfo($name) LIMIT $limit",
                r => new CompletionColumn(r.GetString(0), r.GetString(1), r.GetInt64(2) != 0),
                ("$name", name), ("$limit", CompletionSchema.MaxColumns + 1));
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode != SQLitePCL.raw.SQLITE_INTERRUPT)
        {
            // A view over a dropped table, or a virtual table whose module isn't loaded: the name is
            // still worth completing, its columns just aren't knowable.
            return [];
        }
    }
}
