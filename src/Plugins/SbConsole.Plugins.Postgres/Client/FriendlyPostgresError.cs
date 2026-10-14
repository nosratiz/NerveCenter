using System.Net.Sockets;
using System.Text.RegularExpressions;
using Npgsql;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Postgres.Client;

/// <summary>The only path raw Npgsql exception text takes to the UI or audit rows.</summary>
public static partial class FriendlyPostgresError
{
    private static readonly Dictionary<string, string> Conditions = new()
    {
        ["42703"] = "undefined_column", ["42P01"] = "undefined_table", ["42601"] = "syntax_error",
        ["42883"] = "undefined_function", ["23505"] = "unique_violation", ["23503"] = "foreign_key_violation",
        ["23502"] = "not_null_violation", ["23514"] = "check_violation", ["25006"] = "read_only_sql_transaction",
        ["57014"] = "query_canceled", ["40P01"] = "deadlock_detected", ["55P03"] = "lock_not_available",
        ["42501"] = "insufficient_privilege", ["28P01"] = "invalid_password", ["3D000"] = "invalid_catalog_name",
        ["53300"] = "too_many_connections",
    };

    public static string From(Exception ex) => Describe(ex).Message;

    public static PgErrorInfo Describe(Exception ex, string? sql = null)
    {
        switch (ex)
        {
            case PostgresException pg:
                return DescribePg(pg, sql);
            case PgSqlRefusedException refused:
                return Plain(FriendlyError.Truncate(refused.Message));
            case FormatException format:
                return Plain(FriendlyError.Truncate("The saved connection settings are invalid: " + format.Message));
            case TimeoutException:
                return Failure("Can't reach the server — it didn't answer in time.");
            case NpgsqlException { InnerException: TimeoutException }:
                return Failure("Can't reach the server — it didn't answer in time.");
            case NpgsqlException { InnerException: SocketException socket }:
                return socket.SocketErrorCode == SocketError.TimedOut
                    ? Failure("Can't reach the server — it didn't answer in time.")
                    : Failure("Can't reach the server — the connection was refused.");
            default:
                return Plain(FriendlyError.From(ex));
        }
    }

    private static PgErrorInfo Plain(string message) => new(message, null, null, null, null, null, false);

    private static PgErrorInfo Failure(string message) => new(message, null, null, null, null, null, true);

    private static PgErrorInfo DescribePg(PostgresException pg, string? sql)
    {
        Conditions.TryGetValue(pg.SqlState, out var condition);
        int? line = null;
        int? column = null;
        if (sql is not null && pg.Position > 0)
        {
            (var l, var c) = PgSqlLexer.LineAndColumn(sql, pg.Position);
            line = l;
            column = c;
        }

        return new PgErrorInfo(Sentence(pg), pg.Hint, pg.SqlState, condition, line, column, false);
    }

    private static string Sentence(PostgresException pg)
    {
        var text = pg.MessageText ?? "";
        switch (pg.SqlState)
        {
            case "42703":
            {
                var m = ColumnPattern().Match(text);
                if (!m.Success)
                {
                    return "That column doesn't exist.";
                }

                var qualifier = m.Groups[1].Success ? m.Groups[1].Value : null;
                var column = m.Groups[2].Value;
                var table = !string.IsNullOrEmpty(pg.TableName) ? pg.TableName : qualifier;
                return table is null
                    ? $"Column `{column}` doesn't exist."
                    : $"Column `{column}` doesn't exist on `{table}`.";
            }

            case "42P01":
            {
                var m = RelationRegex().Match(text);
                return m.Success ? $"Table `{m.Groups[1].Value}` doesn't exist." : "That table doesn't exist.";
            }

            case "42601":
            {
                var m = NearRegex().Match(text);
                return m.Success ? $"Syntax error near `{m.Groups[1].Value}`." : "Syntax error.";
            }

            case "25006": return "This is a read-only transaction — switch to Write to change data.";
            case "57014": return "The statement was cancelled — it hit the timeout or someone cancelled it.";
            case "23505": return $"That would duplicate a unique key (`{pg.ConstraintName}`).";
            case "23503": return $"That would break foreign key `{pg.ConstraintName}`.";
            case "23502": return $"Column `{pg.ColumnName}` can't be null.";
            case "23514": return $"Check constraint `{pg.ConstraintName}` failed.";
            case "40P01": return "Deadlock — the server rolled this statement back.";
            case "55P03": return "The row or table is locked by another session.";
            case "42501": return "Permission denied for this role.";
            case "28P01": return "Password authentication failed.";
            case "3D000": return "Database doesn't exist.";
            case "53300": return "The server has no connection slots left.";
            default:
            {
                var message = FriendlyError.Truncate(text);
                return message.Length == 0 ? message : char.ToUpperInvariant(message[0]) + message[1..];
            }
        }
    }

    // column "x" does not exist | column o.x does not exist | column "o.x" ...
    [GeneratedRegex("""column\s+(?:"?([A-Za-z_][\w$]*)"?\.)?"?([^"\s.]+)"?\s+does not exist""", RegexOptions.IgnoreCase)]
    private static partial Regex ColumnPattern();

    [GeneratedRegex("""relation "([^"]+)" does not exist""", RegexOptions.IgnoreCase)]
    private static partial Regex RelationRegex();

    [GeneratedRegex(@"at or near ""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex NearRegex();
}
