using System.Globalization;
using System.Text;
using Npgsql;

namespace SbConsole.Plugins.Postgres.Client;

/// <summary>
/// The saved secret: an Npgsql-style key=value; string plus two house keys the driver does not know
/// (<c>Mode</c>, <c>StatementTimeoutS</c>). Any other Npgsql key is ignored on parse -- the form is the
/// source of truth.
/// </summary>
public sealed record PostgresConnectionSettings(
    string Host,
    int Port = 5432,
    string Database = "postgres",
    string Username = "",
    string Password = "",
    string SslMode = "Prefer",
    int ConnectTimeoutS = 5,
    int StatementTimeoutS = 30,
    PgAccessMode Mode = PgAccessMode.ReadOnly)
{
    public static readonly string[] SslModes = ["Disable", "Prefer", "Require", "VerifyCA", "VerifyFull"];

    public const int MaxTimeoutS = 3600;

    // Npgsql itself rejects Timeout above 1024, so a larger connect timeout could never be built.
    public const int MaxConnectTimeoutS = 1024;

    public string Endpoint => $"{Host}:{Port}";

    public static PostgresConnectionSettings Parse(string secret)
    {
        var mode = PgAccessMode.ReadOnly;
        var statementTimeoutS = 30;
        var npgsqlParts = new List<string>();

        foreach (var segment in SplitSegments(secret ?? ""))
        {
            var eq = segment.IndexOf('=');
            var key = eq < 0 ? "" : segment[..eq].Trim();
            var value = eq < 0 ? "" : segment[(eq + 1)..].Trim();
            if (key.Equals("Mode", StringComparison.OrdinalIgnoreCase))
            {
                if (!Enum.TryParse(Unquote(value), ignoreCase: true, out mode) || !Enum.IsDefined(mode)
                    || int.TryParse(Unquote(value), out _))
                {
                    throw new FormatException($"Unknown Mode '{Unquote(value)}'.");
                }
            }
            else if (key.Equals("StatementTimeoutS", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(Unquote(value), NumberStyles.None, CultureInfo.InvariantCulture, out statementTimeoutS)
                    || statementTimeoutS is < 1 or > MaxTimeoutS)
                {
                    throw new FormatException($"StatementTimeoutS must be a whole number between 1 and {MaxTimeoutS}.");
                }
            }
            else if (IsAcceptedByNpgsql(segment))
            {
                npgsqlParts.Add(segment);
            }
        }

        NpgsqlConnectionStringBuilder b;
        try
        {
            b = new NpgsqlConnectionStringBuilder(string.Join(';', npgsqlParts));
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            throw new FormatException("Invalid connection string.", ex);
        }

        if (string.IsNullOrWhiteSpace(b.Host))
        {
            throw new FormatException("Host is required.");
        }

        if (b.Port is < 1 or > 65535)
        {
            throw new FormatException("Port must be between 1 and 65535.");
        }

        var ssl = FromNpgsqlSslMode(b.SslMode);
        // ShouldSerialize is true only for keys actually given (any alias), so no hand-kept alias list.
        var timeout = 5;
        if (b.ShouldSerialize("Timeout"))
        {
            timeout = b.Timeout;
            if (timeout < 1)
            {
                throw new FormatException("Timeout must be a positive whole number.");
            }
        }

        return new PostgresConnectionSettings(
            b.Host, b.Port, string.IsNullOrWhiteSpace(b.Database) ? "postgres" : b.Database,
            b.Username ?? "", b.Password ?? "", ssl, timeout, statementTimeoutS, mode);
    }

    public string ToSecretString()
    {
        Validate();
        var b = new NpgsqlConnectionStringBuilder
        {
            Host = Host,
            Port = Port,
            Database = Database,
            Username = Username,
            Password = Password,
            SslMode = ToNpgsqlSslMode(SslMode),
            Timeout = ConnectTimeoutS,
        };
        return $"{b.ConnectionString};Mode={Mode};StatementTimeoutS={StatementTimeoutS}";
    }

    public string BuildConnectionString(string? database = null)
    {
        Validate();
        return new NpgsqlConnectionStringBuilder
        {
            Host = Host,
            Port = Port,
            Database = database ?? Database,
            Username = Username,
            Password = Password,
            SslMode = ToNpgsqlSslMode(SslMode),
            Timeout = ConnectTimeoutS,
            ApplicationName = "sbconsole",
            IncludeErrorDetail = false,
            Pooling = true,
            CommandTimeout = StatementTimeoutS + 5,
        }.ConnectionString;
    }

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host))
        {
            throw new ArgumentException("Host is required.", nameof(Host));
        }

        if (Port is < 1 or > 65535)
        {
            throw new ArgumentException("Port must be between 1 and 65535.", nameof(Port));
        }

        if (ConnectTimeoutS is < 1 or > MaxConnectTimeoutS || StatementTimeoutS is < 1 or > MaxTimeoutS)
        {
            throw new ArgumentException($"Timeouts must be between 1 and {MaxTimeoutS} seconds (connect timeout at most {MaxConnectTimeoutS}).");
        }

        if (!SslModes.Contains(SslMode))
        {
            throw new ArgumentException($"Unknown SSL mode '{SslMode}'.", nameof(SslMode));
        }
    }

    private static string FromNpgsqlSslMode(Npgsql.SslMode mode) => mode switch
    {
        Npgsql.SslMode.Disable => "Disable",
        Npgsql.SslMode.Prefer => "Prefer",
        Npgsql.SslMode.Require => "Require",
        Npgsql.SslMode.VerifyCA => "VerifyCA",
        Npgsql.SslMode.VerifyFull => "VerifyFull",
        _ => throw new FormatException($"Unsupported SSL Mode '{mode}'."),
    };

    private static Npgsql.SslMode ToNpgsqlSslMode(string mode) => mode switch
    {
        "Disable" => Npgsql.SslMode.Disable,
        "Prefer" => Npgsql.SslMode.Prefer,
        "Require" => Npgsql.SslMode.Require,
        "VerifyCA" => Npgsql.SslMode.VerifyCA,
        "VerifyFull" => Npgsql.SslMode.VerifyFull,
        _ => throw new ArgumentException($"Unknown SSL mode '{mode}'.", nameof(mode)),
    };

    private static bool IsAcceptedByNpgsql(string segment)
    {
        if (segment.IndexOf('=') < 0)
        {
            return false;
        }

        try
        {
            _ = new NpgsqlConnectionStringBuilder(segment);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            // Unknown keyword: ignored, not preserved (the form is the source of truth).
            // A bad value for a known keyword (e.g. SSL Mode=Bogus) must still fail loudly.
            var key = segment[..segment.IndexOf('=')].Trim();
            return KnownKeyThrows(key);
        }
    }

    // True when the key itself is a real Npgsql keyword (so the exception was about its value).
    private static bool KnownKeyThrows(string key)
    {
        return new NpgsqlConnectionStringBuilder().ContainsKey(key);
    }

    /// <summary>Splits on ';' outside single/double-quoted values (a doubled quote escapes).</summary>
    private static IEnumerable<string> SplitSegments(string s)
    {
        var sb = new StringBuilder();
        char quote = '\0';
        var atValueStart = false;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (quote != '\0')
            {
                sb.Append(c);
                if (c == quote)
                {
                    if (i + 1 < s.Length && s[i + 1] == quote)
                    {
                        sb.Append(s[++i]);
                    }
                    else
                    {
                        quote = '\0';
                    }
                }

                continue;
            }

            if (c == ';')
            {
                if (sb.ToString().Trim().Length > 0)
                {
                    yield return sb.ToString();
                }

                sb.Clear();
                atValueStart = false;
                continue;
            }

            if (c == '=' && !atValueStart && sb.ToString().IndexOf('=') < 0)
            {
                atValueStart = true;
                sb.Append(c);
                continue;
            }

            if (atValueStart && char.IsWhiteSpace(c))
            {
                sb.Append(c);
                continue;
            }

            if (atValueStart && (c == '"' || c == '\''))
            {
                quote = c;
            }

            atValueStart = false;
            sb.Append(c);
        }

        if (sb.ToString().Trim().Length > 0)
        {
            yield return sb.ToString();
        }
    }

    private static string Unquote(string v) =>
        v.Length >= 2 && (v[0] == '"' || v[0] == '\'') && v[^1] == v[0] ? v[1..^1] : v;
}
