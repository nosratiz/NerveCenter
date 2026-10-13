namespace SbConsole.Plugins.Sqlite.Client;

public enum CellKind
{
    Null,
    Integer,
    Real,
    Text,
    Blob,
}

/// <summary>A preview cell, already rendered for display. Integer keeps the raw value so the page can add a date hint.</summary>
public sealed record CellValue(CellKind Kind, string Display, long? Integer = null, bool Truncated = false);

public sealed record RowPage(string Sql, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<CellValue>> Rows)
{
    public const int PreviewLimit = 50;
}

/// <summary>
/// Aggregates over the first N rows of a table, per column (design spec §6 "Notes column").
/// EpochMsCount = INTEGER values inside 2000-01-01..2100-01-01 as unix milliseconds.
/// JsonCount = TEXT values whose first non-whitespace character is '{' or '[' and that pass
/// json_valid; bare JSON scalars (123, true) are valid JSON but deliberately not counted, since
/// the note is about JSON documents.
/// </summary>
public sealed record ColumnSample(
    string Column, int SampledRows, int NonNullCount, int DistinctCount, int TextCount, int JsonCount, int IntegerCount, int EpochMsCount)
{
    public const int DefaultSampleRows = 1000;
    public const long EpochMsMin = 946_684_800_000;
    public const long EpochMsMax = 4_102_444_800_000;
}
