namespace SbConsole.Plugins.Sqlite.Client;

/// <summary>
/// A whole table as CSV, built in memory and handed to the browser through a DotNetStreamReference
/// (the host has no plugin-endpoint seam -- design spec §6), hence the hard row cap.
/// </summary>
public sealed record CsvExport(string FileName, byte[] Content, int RowCount, bool Truncated)
{
    public const int MaxRows = 100_000;
}
