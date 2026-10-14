using System.Globalization;

namespace SbConsole.Plugins.Sqlite.Client;

/// <summary>Named "access" mode so it doesn't collide with Microsoft.Data.Sqlite.SqliteOpenMode.</summary>
public enum SqliteAccessMode
{
    ReadOnly,
    ReadWrite,
}

/// <summary>
/// The saved secret, house key=value; convention (design spec §4.1):
/// "Path=/data/reports.db;Mode=ReadOnly;BusyTimeoutMs=5000;StatementTimeoutS=30". Nothing in it is
/// credential-shaped, but it is stored through the normal encrypted secret path anyway. Mode is
/// stored so phase 2 (write mode) needs no migration; phase 1 always opens read-only. BackupDir
/// (design spec §11.7) is optional and only written when set: null = backups disabled.
/// </summary>
public sealed record SqliteConnectionSettings(
    string Path,
    SqliteAccessMode Mode = SqliteAccessMode.ReadOnly,
    int BusyTimeoutMs = SqliteConnectionSettings.DefaultBusyTimeoutMs,
    int StatementTimeoutS = SqliteConnectionSettings.DefaultStatementTimeoutS,
    string? BackupDir = null)
{
    public const int DefaultBusyTimeoutMs = 5000;
    public const int DefaultStatementTimeoutS = 30;

    public static SqliteConnectionSettings Parse(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in secret.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = part.IndexOf('=');
            if (equals <= 0)
            {
                throw new FormatException($"Expected key=value, got '{part.Trim()}'.");
            }

            values[part[..equals].Trim()] = part[(equals + 1)..].Trim();
        }

        if (!values.TryGetValue("Path", out var path) || path.Length == 0)
        {
            throw new FormatException("Path is required.");
        }

        var mode = SqliteAccessMode.ReadOnly;
        if (values.TryGetValue("Mode", out var modeText))
        {
            // Exact name match only: Enum.TryParse would also accept "A,B" flag syntax and numbers.
            var name = Enum.GetNames<SqliteAccessMode>().FirstOrDefault(n => string.Equals(n, modeText, StringComparison.OrdinalIgnoreCase))
                ?? throw new FormatException($"Unknown Mode '{modeText}'.");
            mode = Enum.Parse<SqliteAccessMode>(name);
        }

        return new SqliteConnectionSettings(
            path,
            mode,
            PositiveInt(values, "BusyTimeoutMs", DefaultBusyTimeoutMs),
            PositiveInt(values, "StatementTimeoutS", DefaultStatementTimeoutS),
            values.TryGetValue("BackupDir", out var backupDir) && backupDir.Length > 0 ? backupDir : null);
    }

    public string ToSecretString()
    {
        if (string.IsNullOrWhiteSpace(Path))
        {
            throw new ArgumentException("Path is required.", nameof(Path));
        }

        if (Path.Contains(';'))
        {
            throw new ArgumentException("Path can't contain ';'.", nameof(Path));
        }

        if (BusyTimeoutMs <= 0 || StatementTimeoutS <= 0)
        {
            throw new ArgumentException("Timeouts must be positive.");
        }

        if (BackupDir is not null && BackupDir.Contains(';'))
        {
            throw new ArgumentException("BackupDir can't contain ';'.", nameof(BackupDir));
        }

        var secret = string.Create(CultureInfo.InvariantCulture,
            $"Path={Path};Mode={Mode};BusyTimeoutMs={BusyTimeoutMs};StatementTimeoutS={StatementTimeoutS}");
        return string.IsNullOrWhiteSpace(BackupDir) ? secret : $"{secret};BackupDir={BackupDir.Trim()}";
    }

    private static int PositiveInt(Dictionary<string, string> values, string key, int fallback)
    {
        if (!values.TryGetValue(key, out var text))
        {
            return fallback;
        }

        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0)
        {
            throw new FormatException($"{key} must be a positive whole number.");
        }

        return value;
    }
}
