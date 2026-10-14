using System.Globalization;
using Microsoft.Data.Sqlite;

namespace SbConsole.Plugins.Sqlite.Client;

/// <summary>Small synchronous command helpers. Values always travel as parameters; identifiers only through QuoteIdentifier.</summary>
internal static class SqliteSql
{
    public static void Execute(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Create(connection, sql, parameters);
        command.ExecuteNonQuery();
    }

    public static object? Scalar(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Create(connection, sql, parameters);
        return command.ExecuteScalar();
    }

    public static long Long(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters) =>
        Scalar(connection, sql, parameters) is { } value and not DBNull ? Convert.ToInt64(value, CultureInfo.InvariantCulture) : 0;

    public static string Text(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters) =>
        Convert.ToString(Scalar(connection, sql, parameters), CultureInfo.InvariantCulture) ?? "";

    public static List<T> Rows<T>(SqliteConnection connection, string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] parameters)
    {
        using var command = Create(connection, sql, parameters);
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read())
        {
            rows.Add(map(reader));
        }

        return rows;
    }

    /// <summary>SQLite identifier quoting: wrap in double quotes, double any embedded double quote.</summary>
    public static string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static SqliteCommand Create(SqliteConnection connection, string sql, (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }
}
