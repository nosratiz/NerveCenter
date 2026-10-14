namespace SbConsole.Core.Settings;

/// <summary>The ONLY configuration read from environment variables. Everything else lives in AppSettings rows.</summary>
public sealed record BootstrapOptions(string DbPath, byte[] DataKey, string AdminPassword, string ApiKey, string Bind)
{
    /// <summary>
    /// Directories SQLite connections may open files under (SBC_SQLITE_ROOTS, ';'-separated).
    /// Empty means no SQLite file can be opened at all -- HostPathPolicy fails closed.
    /// </summary>
    public IReadOnlyList<string> SqliteRoots { get; init; } = [];

    /// <summary>
    /// Optional JSON file of demo connections to create at startup if missing (SBC_DEMO_CONNECTIONS).
    /// Null means the feature is off. Demo/dev convenience only -- see docs/design.md §16.
    /// </summary>
    public string? DemoConnectionsFile { get; init; }

    public static BootstrapOptions FromEnvironment(Func<string, string?> getEnv)
    {
        string Require(string name) =>
            getEnv(name) ?? throw new InvalidOperationException($"Missing required environment variable {name}.");

        byte[] dataKey;
        try
        {
            dataKey = Convert.FromBase64String(Require("SBC_DATA_KEY"));
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("SBC_DATA_KEY must be base64-encoded (32 bytes).");
        }

        if (dataKey.Length != 32)
        {
            throw new InvalidOperationException("SBC_DATA_KEY must decode to exactly 32 bytes.");
        }

        return new BootstrapOptions(
            DbPath: Require("SBC_DB_PATH"),
            DataKey: dataKey,
            AdminPassword: Require("SBC_ADMIN_PASSWORD"),
            ApiKey: Require("SBC_API_KEY"),
            Bind: getEnv("SBC_BIND") ?? "http://localhost:5080")
        {
            SqliteRoots = ParseRoots(getEnv("SBC_SQLITE_ROOTS")),
            DemoConnectionsFile = getEnv("SBC_DEMO_CONNECTIONS") is { } demo && !string.IsNullOrWhiteSpace(demo)
                ? demo.Trim()
                : null,
        };
    }

    private static IReadOnlyList<string> ParseRoots(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public override string ToString() =>
        $"BootstrapOptions {{ DbPath = {DbPath}, DataKey = [REDACTED], AdminPassword = [REDACTED], ApiKey = [REDACTED], Bind = {Bind}, SqliteRoots = [{string.Join(";", SqliteRoots)}], DemoConnectionsFile = {DemoConnectionsFile ?? "(off)"} }}";
}
