namespace SbConsole.Plugins.Postgres.Client;

/// <summary>
/// A whole table (or a query result) as CSV, built in memory and handed to the browser through a DotNetStreamReference
/// (the host has no plugin-endpoint seam -- design spec §6), hence the hard row cap.
/// </summary>
public sealed record CsvExport(string FileName, byte[] Content, int RowCount, bool Truncated)
{
    public const int MaxRows = 100_000;
    public const long MaxBytes = 50L * 1024 * 1024;

    /// <summary>Tests only: lowers the row cap so truncation can be exercised without 100,000 rows. Always reset in a finally.</summary>
    internal static int? MaxRowsOverride { get; set; }

    internal static int EffectiveMaxRows => MaxRowsOverride ?? MaxRows;

    /// <summary>"1,234 rows", plus which cap truncated it -- the footer and the audit detail.</summary>
    public string Summary => $"{PgFormat.Count(RowCount)} rows"
        + (Truncated
            ? RowCount >= EffectiveMaxRows
                ? $" (truncated at {PgFormat.Count(EffectiveMaxRows)} rows)"
                : $" (truncated at {PgFormat.Bytes(MaxBytes)})"
            : "");
}
